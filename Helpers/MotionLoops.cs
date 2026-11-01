using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 少量「一直动」的循环动效（呼吸点）。
///
/// <para>
/// <b>为什么单独一个文件</b>：循环动效是全应用里最容易被滥用的东西 ——
/// 一屏两个就会互相抢注意力。这里集中收口，并强制一条规则：
/// <b>同屏只允许一个循环</b>。壁纸呼吸（用户可选）开着时，这里的呼吸点一律不启动。
/// </para>
///
/// <para>
/// 用代码而不是 XAML 的 <c>Style.Animations</c>，是因为它的启停完全由业务状态决定
/// （联机服务在跑才呼吸），样式表拿不到这个信号；
/// 而且 <c>Style.Animations</c> 一旦挂上就会一直循环，没有"停"的入口。
/// </para>
/// </summary>
public static class MotionLoops
{
    /// <summary>一个元素当前挂着的循环动效令牌（停的时候要取消它）。</summary>
    private static readonly ConditionalWeakTable<Visual, CancellationTokenSource> Running = new();

    /// <summary>呼吸周期：2 秒一个来回。比这快会心慌，比这慢就看不出来。</summary>
    private static readonly TimeSpan PulseDuration = TimeSpan.FromSeconds(2);

    /// <summary>呼吸的最低不透明度。低于 0.3 会像"坏了在闪"。</summary>
    private const double PulseMinOpacity = 0.35d;

    /// <summary>
    /// 打开 / 关闭某个元素的呼吸效果。
    /// </summary>
    /// <param name="visual">目标元素（联机状态点这类）。</param>
    /// <param name="enabled">业务上是否应该呼吸。</param>
    public static void SetPulse(Visual? visual, bool enabled)
    {
        if (visual is null)
            return;

        Stop(visual);

        if (!enabled)
            return;

        // 动效关闭 / 精简档：不呼吸（呼吸属于纯装饰）
        if (!MotionSettings.AllowMovement)
            return;

        // 同屏只留一个循环：壁纸已经在呼吸了，状态点就安静待着
        if (SettingsService.Current.BackgroundAnimated)
            return;

        var cts = new CancellationTokenSource();
        Running.Add(visual, cts);

        var animation = new Animation
        {
            Duration = PulseDuration,
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut()
        };

        var dim = new KeyFrame { Cue = new Cue(0d) };
        dim.Setters.Add(new Setter(Visual.OpacityProperty, 1d));

        var fade = new KeyFrame { Cue = new Cue(1d) };
        fade.Setters.Add(new Setter(Visual.OpacityProperty, PulseMinOpacity));

        animation.Children.Add(dim);
        animation.Children.Add(fade);

        _ = animation.RunAsync(visual, cts.Token);
    }

    /// <summary>停掉某个元素的呼吸（并把不透明度复位到 1，否则会停在半透明上）。</summary>
    public static void Stop(Visual? visual)
    {
        if (visual is null)
            return;

        if (Running.TryGetValue(visual, out var cts))
        {
            Running.Remove(visual);
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已经释放过：什么都不用做
            }

            cts.Dispose();
        }

        // 动画被取消时不一定把属性还回去，显式复位
        visual.Opacity = 1d;
    }
}
