using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Threading;
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
/// <b>为什么不用 <c>Animation.RunAsync</c></b>：Avalonia 的 <c>RunAsync</c> 只接受有限次的
/// 动画，把 <c>IterationCount.Infinite</c> 交给它会当场抛
/// <c>InvalidOperationException: Looping animations must not use the Run method</c>。
/// 而这里的 <c>_ = animation.RunAsync(...)</c> 是丢弃 Task 的，异常变成「未观察的任务异常」，
/// 每 20 秒由终结器报一次 —— 日志里那一串同名 ERR 就是它，呼吸效果本身也一次都没生效。
/// </para>
///
/// <para>
/// 所以改用 <see cref="DispatcherTimer"/> 自己算余弦呼吸：启停完全可控，
/// 只写一个 <c>Opacity</c>（合成层属性，不触发重排），也不需要跟动画调度器打交道。
/// XAML 里的无限循环（壁纸）走 <c>Style.Animations</c>，那是另一条合法路径，不受这条限制。
/// </para>
/// </summary>
public static class MotionLoops
{
    /// <summary>一个元素当前挂着的呼吸（停的时候要释放它）。</summary>
    private static readonly ConditionalWeakTable<Visual, Breathing> Running = new();

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

        // DispatcherTimer 必须在 UI 线程创建；联机状态可能在后台线程变化
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetPulse(visual, enabled));
            return;
        }

        Stop(visual);

        if (!enabled)
            return;

        // 动效关闭 / 精简档：不呼吸（呼吸属于纯装饰）
        if (!MotionSettings.AllowMovement)
            return;

        // 同屏只留一个循环：壁纸已经在呼吸了，状态点就安静待着
        if (SettingsService.Current.BackgroundAnimated)
            return;

        Running.Add(visual, new Breathing(visual));
    }

    /// <summary>停掉某个元素的呼吸（并把不透明度复位到 1，否则会停在半透明上）。</summary>
    public static void Stop(Visual? visual)
    {
        if (visual is null)
            return;

        if (Running.TryGetValue(visual, out var breathing))
        {
            Running.Remove(visual);
            breathing.Dispose();
        }

        // 释放时已经复位过，这里再写一次是防止「没在呼吸也被调 Stop」时残留半透明
        visual.Opacity = 1d;
    }

    /// <summary>
    /// 单个元素的呼吸状态。每 40ms 写一次 Opacity（约 25fps，肉眼足够顺滑），
    /// 用余弦而不是线性往返，避免最高点和最低点出现折角。
    /// </summary>
    private sealed class Breathing : IDisposable
    {
        private readonly Visual _target;
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _disposed;

        public Breathing(Visual target)
        {
            _target = target;
            _timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) => Tick());
            _timer.Start();
        }

        private void Tick()
        {
            if (_disposed)
                return;

            var phase = _clock.Elapsed.TotalMilliseconds % PulseDuration.TotalMilliseconds;
            var progress = phase / PulseDuration.TotalMilliseconds;   // 0 → 1
            var wave = 0.5d - 0.5d * Math.Cos(progress * Math.PI * 2); // 0 → 1 → 0
            _target.Opacity = 1d - (1d - PulseMinOpacity) * wave;
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Stop();
            _target.Opacity = 1d;
        }
    }
}
