using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 「飞入 / 飞出」动画的统一入口。
///
/// <para>
/// 本工程的「窗口」有两种形态，两者共用这一套节奏，观感才一致：
/// </para>
/// <list type="bullet">
///   <item>
///     <b>独立窗口</b>（主窗口、开机向导）—— 内容是窗口内的一整块根面板，
///     打开时整块飞入，关闭时整块飞出后再真正 Close。
///   </item>
///   <item>
///     <b>内嵌子窗口</b>（登录、换肤、崩溃分析…几十个 <c>*Window.axaml</c>，
///     其实是 <c>UserControl</c>）—— 由 <see cref="Services.InlineHost"/> 浮在主窗口
///     内容区上，飞入飞出作用在 Floating 卡片与遮罩上。
///   </item>
/// </list>
///
/// <para>
/// <b>为什么不在每个窗口里各写一份</b>：位移量、缩放比、缓动、时长只要有一处不同，
/// 用户就会觉得「有的窗口顺、有的窗口卡」。这里集中定义，改一处全局生效。
/// </para>
///
/// <para>
/// <b>性能模式下一律不播</b>（见 <see cref="Enabled"/>）：低配机器上动画一卡就会
/// 长时间停在中途，观感比没有动画更糟。
/// </para>
/// </summary>
public static class WindowMotion
{
    /// <summary>
    /// 飞入时长。比页面切换（260ms）长，因为是「窗口整体出现」。
    ///
    /// 1.0.9.7：300ms → 450ms。原时长在缩放到位的最后一段收得太急，
    /// 观感像「啪一下贴上去」；拉长后推近的过程看得清。
    /// </summary>
    public static readonly TimeSpan FlyInDuration = TimeSpan.FromMilliseconds(450);

    /// <summary>
    /// 飞出时长。收尾要干脆，太长会让人觉得「点了关闭没反应」。
    ///
    /// 1.0.9.7：180ms → 280ms。180ms 时窗口是「闪一下就没了」，
    /// 下沉 + 后退的过程几乎看不见；280ms 仍在「立刻响应」的感知范围内。
    /// 1.0.9.10：对齐 50ms 时长网格，280 → 300。
    /// </summary>
    public static readonly TimeSpan FlyOutDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>飞入起点缩放（92% → 100%，能明显看出「推近」但不夸张）。</summary>
    private const double FlyInScale = 0.92d;

    /// <summary>飞入起点纵向位移（px，从下方浮上来）。</summary>
    private const double FlyInOffsetY = 26d;

    /// <summary>飞出终点缩放（轻微后退，与飞入对称）。</summary>
    private const double FlyOutScale = 0.94d;

    /// <summary>飞出终点纵向位移（px，向下沉走）。</summary>
    private const double FlyOutOffsetY = 18d;

    /// <summary>是否启用动效。性能模式下关掉（统一入口见 <see cref="MotionSettings"/>）。</summary>
    public static bool Enabled => MotionSettings.Enabled;

    /// <summary>
    /// 把元素预先摆到「飞入起点」（透明 + 缩小 + 下移）。
    ///
    /// <para>
    /// 必须在窗口内容首次渲染**之前**调用（通常放在构造函数末尾）。否则用户会先看到
    /// 一帧完整的界面，然后它突然变透明重新飞入 —— 那一下闪烁比不做动画更刺眼。
    /// </para>
    /// </summary>
    public static void PrepareFlyIn(Visual? root)
    {
        if (root is not Control control)
            return;

        if (!Enabled)
        {
            AnimationHelper.ApplyEndState(control, 1d, 1d, 0d);
            return;
        }

        AnimationHelper.ApplyEndState(control, 0d, FlyInScale, FlyInOffsetY);
    }

    /// <summary>
    /// 播一次飞入：淡入 + 92% 推到 100% + 由下浮起。
    /// </summary>
    /// <param name="root">要飞入的根元素（窗口内容 / 悬浮卡片）。</param>
    /// <param name="scrim">可选的遮罩层，会同步从透明淡到不透明。</param>
    /// <param name="cancellationToken">取消（例如页面还没飞完就被新页面顶掉）。</param>
    public static Task FlyInAsync(
        Visual? root,
        Visual? scrim = null,
        CancellationToken cancellationToken = default)
    {
        if (root is not Control control)
            return Task.CompletedTask;

        if (!Enabled)
        {
            AnimationHelper.ApplyEndState(control, 1d, 1d, 0d);
            if (scrim is Visual scrimVisual)
                scrimVisual.Opacity = 1d;

            return Task.CompletedTask;
        }

        // 起点必须是「当前可见的起点」，不能假设调用方已经 PrepareFlyIn 过：
        // 内嵌页每次打开都是新实例，复用同一个卡片容器，上一轮的终态会残留。
        AnimationHelper.ApplyEndState(control, 0d, FlyInScale, FlyInOffsetY);

        var motion = AnimationHelper.AnimateAsync(
            control,
            fromOpacity: 0d, toOpacity: 1d,
            fromScale: FlyInScale, toScale: 1d,
            fromOffsetY: FlyInOffsetY, toOffsetY: 0d,
            FlyInDuration,
            easing: new CubicEaseOut(),
            cancellationToken: cancellationToken);

        if (scrim is Visual scrimLayer)
        {
            scrimLayer.Opacity = 0d;
            return Task.WhenAll(motion, FadeAsync(scrimLayer, 0d, 1d, FlyInDuration, new CubicEaseOut(), cancellationToken));
        }

        return motion;
    }

    /// <summary>
    /// 播一次飞出：淡出 + 100% 退到 94% + 向下沉走。
    /// 播完后元素回到完全可见的终态 —— 调用方随后会把它隐藏 / 卸载，
    /// 下次再打开时不会带着「透明 + 缩小」的残留。
    /// </summary>
    public static async Task FlyOutAsync(
        Visual? root,
        Visual? scrim = null,
        CancellationToken cancellationToken = default)
    {
        if (root is not Control control || !Enabled)
            return;

        var motion = AnimationHelper.AnimateAsync(
            control,
            fromOpacity: 1d, toOpacity: 0d,
            fromScale: 1d, toScale: FlyOutScale,
            fromOffsetY: 0d, toOffsetY: FlyOutOffsetY,
            FlyOutDuration,
            easing: new CubicEaseIn(),
            cancellationToken: cancellationToken);

        if (scrim is Visual scrimLayer)
        {
            await Task.WhenAll(
                motion,
                FadeAsync(scrimLayer, 1d, 0d, FlyOutDuration, new CubicEaseIn(), cancellationToken));
        }
        else
        {
            await motion;
        }

        // 复位：AnimateAsync 会把基准值写成终态（透明 + 0.94），不复位的话
        // 同一个容器下次复用时得靠 FlyInAsync 再摆一次，容易漏。
        AnimationHelper.ApplyEndState(control, 1d, 1d, 0d);
        if (scrim is Visual layer)
            layer.Opacity = 1d;
    }

    /// <summary>
    /// 关窗时最多为动画等多久。超过就直接关，不再等。
    ///
    /// <para>
    /// <b>为什么必须有这个上限（2026-10-21）</b>：动画靠渲染循环推进，而关窗瞬间
    /// 渲染循环可能已经停摆（窗口被隐藏、合成器退出、驱动挂起）。这时候
    /// <c>RunAsync</c> 返回的 Task **永远不会有结果**，<c>await</c> 就永久挂起 ——
    /// 窗口卡着不关、进程赖在后台不走。
    /// </para>
    ///
    /// <para>
    /// 后果比「关不掉」严重得多：僵尸进程会一直占着单文件自解压出来的
    /// <c>%TEMP%\.net\…\*.dll</c>，用户下次双击时宿主解压失败、**没有任何提示地退出**，
    /// 表现就是「这台机器上只能运行一次，之后再也打不开」。
    /// </para>
    /// </summary>
    private static readonly TimeSpan CloseFlyOutTimeout = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// 给独立窗口挂上「关闭时先飞出、再真正关闭」。
    ///
    /// <para>
    /// 做法是拦截 <see cref="Window.Closing"/>：先把 <c>e.Cancel</c> 置 true 拖住关闭，
    /// 播完动画再自己调一次 <see cref="Window.Close"/>。第二次进入时标志已置位，直接放行。
    /// </para>
    ///
    /// <para>
    /// <b>系统级关闭一律不拦</b>（关机、应用退出）：那时候用户要的是立刻退出，
    /// 我们再拖 280ms 只会被当成卡死。
    /// </para>
    /// </summary>
    public static void AttachCloseFlyOut(Window window, Control root, Visual? scrim = null)
    {
        if (window is null || root is null)
            return;

        // 性能模式：完全不挂，连事件订阅都不做
        if (!Enabled)
            return;

        var closing = false;

        window.Closing += async (_, e) =>
        {
            if (closing)
                return;

            if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
                return;

            // 已经被别人拦下并接管了的关闭（例如 MainWindow 把窗口「收进托盘」）：
            // 既不要再播飞出动效，更不能在这里补一次 window.Close() 把它真关掉。
            if (e.Cancel)
                return;

            closing = true;
            e.Cancel = true;

            try
            {
                // 等动画，但**绝不死等**：超时就直接往下走（见 CloseFlyOutTimeout 的说明）。
                // WhenAny 不会取消动画本身，只是不再等它 —— 窗口随后就关了，动画自然失效。
                await Task.WhenAny(FlyOutAsync(root, scrim), Task.Delay(CloseFlyOutTimeout));
            }
            catch (OperationCanceledException)
            {
                // 被取消也要继续关，否则窗口就关不掉了
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "窗口飞出动画失败，直接关闭：{Window}", window.GetType().Name);
            }

            window.Close();
        };
    }

    /// <summary>把元素直接摆回完全可见的终态（跳过动画时用）。</summary>
    public static void Reset(Visual? root)
    {
        if (root is not Control control)
            return;

        AnimationHelper.ApplyEndState(control, 1d, 1d, 0d);
    }

    private static Task FadeAsync(
        Visual visual,
        double from,
        double to,
        TimeSpan duration,
        Easing easing,
        CancellationToken cancellationToken)
        => AnimationHelper.AnimateAsync(
            visual,
            fromOpacity: from, toOpacity: to,
            fromScale: 1d, toScale: 1d,
            fromOffsetY: 0d, toOffsetY: 0d,
            duration,
            easing,
            cancellationToken);
}
