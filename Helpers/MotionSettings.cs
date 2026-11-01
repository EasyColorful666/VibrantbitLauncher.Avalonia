using System;
using Avalonia.Animation.Easings;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 全应用动效的统一闸门与时序表。
///
/// <para>
/// 档位来自「外观 → 动效」，三档：完整 / 精简 / 关闭。性能模式是更粗的总开关，
/// 它开着时一律按「关闭」处理 —— 低配机器上动画一卡就会长时间停在中途，
/// 观感比没有动画更糟。
/// </para>
///
/// <para>
/// <b>为什么需要这个类</b>：动效散落在三处（窗口、页面、列表），
/// 每处各自读 <see cref="SettingsService.Current"/> 的话，
/// 以后要加「只关动效不关壁纸」这种开关就得改三个地方。统一走这里，只改一行。
/// </para>
///
/// <para>
/// XAML 侧对应的关闭规则写在 <c>Styles/Animations.axaml</c> 的
/// <c>.performanceMode</c> / <c>.motionReduced</c> 系列样式里
/// （窗口根节点挂这个类，见 <c>MainWindow.ApplyMotionClasses</c>）。
/// </para>
///
/// <para>
/// <b>时序表</b>（与 <c>Styles/AppTheme.axaml</c> 的缓动资源同源，改一处要两边一起改）：
/// 反馈 100ms · 转场进 250ms / 出 150ms · 窗口进 300ms / 出 200ms。
/// 全部落在 50ms 阶梯上，不写 120 / 180 / 260 这类"看着顺眼"的数。
/// </para>
/// </summary>
public static class MotionSettings
{
    // ==================== 时长（毫秒，50ms 阶梯） ====================

    /// <summary>悬停 / 按住的颜色反馈。</summary>
    public const double FeedbackMs = 100d;

    /// <summary>转场进场（内容落定）。</summary>
    public const double EnterMs = 250d;

    /// <summary>转场出场（内容快走）。必须比进场短，否则会等它。</summary>
    public const double ExitMs = 150d;

    /// <summary>窗口 / 浮层进场。</summary>
    public const double WindowEnterMs = 300d;

    /// <summary>窗口 / 浮层出场。</summary>
    public const double WindowExitMs = 200d;

    /// <summary>列表相邻两项的启动间隔。20ms 是"看得出是一条条"的最小值。</summary>
    public const double StaggerMs = 20d;

    // ==================== 曲线 ====================

    /// <summary>反馈曲线：进出对称，只用于"我点到了"。</summary>
    public static Easing Feedback { get; } = new SplineEasing(0.33d, 0d, 0.67d, 1d);

    /// <summary>进场曲线：先快后慢，内容"落定"。</summary>
    public static Easing Enter { get; } = new SplineEasing(0.05d, 0.7d, 0.1d, 1d);

    /// <summary>出场曲线：先慢后快，内容"快走"。</summary>
    public static Easing Exit { get; } = new SplineEasing(0.3d, 0d, 1d, 1d);

    // ==================== 档位 ====================

    /// <summary>当前生效的档位：Full / Reduced / Off。</summary>
    public static string Level =>
        SettingsService.Current.PerformanceMode ? "Off" : SettingsService.AnimationLevel;

    /// <summary>是否允许播放动效。</summary>
    public static bool Enabled => Level != "Off";

    /// <summary>是否需要禁用过渡（与 <see cref="Enabled"/> 相反，给 XAML / 外部调用更直观的语义）。</summary>
    public static bool DisableTransitions => !Enabled;

    /// <summary>
    /// 精简档：保留「有 / 没有」的反馈，砍掉位移与缩放。
    ///
    /// <para>
    /// 判断依据是「这一下动画是否在表达状态变化」：按钮按下变暗是反馈，留着；
    /// 卡片浮起、页面滑入是装饰，去掉。
    /// </para>
    /// </summary>
    public static bool Reduced => Level == "Reduced";

    /// <summary>是否需要播放位移 / 缩放类动效（关闭与精简都为 false）。</summary>
    public static bool AllowMovement => Enabled && !Reduced;

    /// <summary>动画时长倍率：速度越快，时长越短。手写动画的时长都要乘它。</summary>
    public static double DurationScale => 1.0 / SettingsService.AnimationSpeed;

    /// <summary>页面切换方式：Slide / Fade / None（关掉动效时强制 None）。</summary>
    public static string PageTransition =>
        Enabled ? SettingsService.PageTransitionMode : "None";

    /// <summary>把基准时长按当前速度缩放。</summary>
    public static TimeSpan Scale(TimeSpan baseline) =>
        TimeSpan.FromMilliseconds(baseline.TotalMilliseconds * DurationScale);

    /// <summary>
    /// 按毫秒取一个已应用「速度倍率」的时长；动效关闭时返回 <see cref="TimeSpan.Zero"/>，
    /// 调用方拿到零时长就会直接落到终态，不必自己再判一遍档位。
    /// </summary>
    public static TimeSpan Ms(double milliseconds) =>
        Enabled ? TimeSpan.FromMilliseconds(milliseconds * DurationScale) : TimeSpan.Zero;

    /// <summary>
    /// 把当前档位挂成窗口根节点上的类名，供 <c>Styles/Animations.axaml</c> 命中。
    ///
    /// <para>
    /// 每个独立的 <see cref="Avalonia.Controls.Window"/> 都要各自调一次：
    /// 类名不是继承属性，子窗口不会跟着主窗口走。
    /// 目前只有三个真窗口（主窗口 / 首次使用向导 / 游戏内工具箱），
    /// 其余「窗口」都是挂在它们里面的浮层，天然被祖先的类名覆盖。
    /// </para>
    /// </summary>
    public static void ApplyClasses(Avalonia.StyledElement element)
    {
        element.Classes.Remove("performanceMode");
        element.Classes.Remove("motionReduced");

        var level = Level;
        if (level == "Off")
            element.Classes.Add("performanceMode");
        else if (level == "Reduced")
            element.Classes.Add("motionReduced");
    }
}
