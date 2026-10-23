using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 轻提示服务：在窗口右下角弹出一条 InfoBar，若干秒后自动消失。
    /// 替代 Wpf.Ui 的 ISnackbarService。
    ///
    /// <para>
    /// <b>宿主必须是 StackPanel</b>：早期这里是 <see cref="Panel"/>，
    /// 而 Panel 会把所有子元素摆在同一位置 —— 两条通知同时存在时文字直接重影。
    /// 现在由 StackPanel 纵向堆叠，宿主类型见 MainWindow.axaml 的 SnackbarHost。
    /// </para>
    /// </summary>
    public sealed class NotificationService : INotificationService
    {
        private readonly Panel _host;
        private readonly List<Snack> _snacks = new();

        /// <summary>同时最多保留几条（超出时最旧的先飞出去）。</summary>
        private const int MaxVisible = 4;

        private const double EnterOffsetX = 28;
        private const double ExitOffsetX = 28;
        private const int EnterMs = 190;
        private const int ExitMs = 150;

        public NotificationService(Panel host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public void Show(string message, string? title = null, NotificationSeverity severity = NotificationSeverity.Informational)
        {
            void Post()
            {
                var resolvedTitle = title ?? string.Empty;
                var key = $"{resolvedTitle}\u0001{message}\u0001{severity}";

                // 同一条提示重复到达（例如连续两次启动失败）时不再叠一条，
                // 只把已有那条的倒计时重新拉满 —— 否则四条一模一样的通知排队飞入。
                var existing = _snacks.Find(s => !s.Closing && s.Key == key);
                if (existing is not null)
                {
                    RestartTimer(existing);
                    return;
                }

                // 超出上限：让最旧的先走（带飞出动画，不是硬删）
                while (CountAlive() >= MaxVisible)
                {
                    var oldest = _snacks.Find(s => !s.Closing);
                    if (oldest is null)
                        break;
                    Dismiss(oldest, animated: true);
                }

                var bar = new FAInfoBar
                {
                    Message = message,
                    Title = resolvedTitle,
                    Severity = Map(severity),
                    IsOpen = true,
                    IsClosable = true,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MinWidth = 280,
                    MaxWidth = 420,
                    Margin = new Thickness(0, 0, 16, 8)
                };

                var snack = new Snack { Bar = bar, Key = key, Severity = severity };
                bar.CloseButtonClick += (_, _) => Dismiss(snack, animated: false);

                // 起点先摆好，再加入可视树：否则会先闪一帧完整内容再跳回去
                if (MotionSettings.Enabled)
                    AnimationHelper.PrepareSlideX(bar, 0, EnterOffsetX);

                _snacks.Add(snack);
                _host.Children.Add(bar);

                if (MotionSettings.Enabled)
                {
                    _ = AnimationHelper.AnimateSlideXAsync(
                        bar,
                        fromOpacity: 0, toOpacity: 1,
                        fromOffsetX: EnterOffsetX, toOffsetX: 0,
                        duration: TimeSpan.FromMilliseconds(EnterMs));
                }

                RestartTimer(snack);
            }

            if (Dispatcher.UIThread.CheckAccess())
                Post();
            else
                Dispatcher.UIThread.Post(Post);
        }

        public void Success(string message, string? title = null) => Show(message, title ?? "成功", NotificationSeverity.Success);

        public void Warning(string message, string? title = null) => Show(message, title ?? "提示", NotificationSeverity.Warning);

        public void Error(string message, string? title = null) => Show(message, title ?? "错误", NotificationSeverity.Error);

        /// <summary>正在场上的条数（不含正在飞出的）。</summary>
        private int CountAlive()
        {
            var count = 0;
            foreach (var s in _snacks)
            {
                if (!s.Closing)
                    count++;
            }
            return count;
        }

        /// <summary>（重新）开始倒计时。错误停留更久，因为要读的内容更长。</summary>
        private void RestartTimer(Snack snack)
        {
            snack.Timer?.Stop();

            var seconds = snack.Severity == NotificationSeverity.Error ? 6 : 4;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => Dismiss(snack, animated: true);
            snack.Timer = timer;
            timer.Start();
        }

        /// <summary>
        /// 移走一条通知。
        /// <para>
        /// 自动到时走「淡出 + 右移」再移除；用户点 × 则立刻移除 ——
        /// 点关闭本来就是要它马上消失，再拖 150ms 只会显得迟钝。
        /// </para>
        /// </summary>
        private async void Dismiss(Snack snack, bool animated)
        {
            if (snack.Closing)
                return;

            snack.Closing = true;
            snack.Timer?.Stop();
            _snacks.Remove(snack);

            if (animated && MotionSettings.Enabled)
            {
                try
                {
                    await AnimationHelper.AnimateSlideXAsync(
                        snack.Bar,
                        fromOpacity: 1, toOpacity: 0,
                        fromOffsetX: 0, toOffsetX: ExitOffsetX,
                        duration: TimeSpan.FromMilliseconds(ExitMs),
                        easing: new CubicEaseIn());
                }
                catch (Exception ex)
                {
                    // 通知消失失败不该影响任何业务，记一笔就继续移除
                    Serilog.Log.Debug(ex, "通知飞出动画失败");
                }
            }

            _host.Children.Remove(snack.Bar);
        }

        private static FAInfoBarSeverity Map(NotificationSeverity severity) => severity switch
        {
            NotificationSeverity.Success => FAInfoBarSeverity.Success,
            NotificationSeverity.Warning => FAInfoBarSeverity.Warning,
            NotificationSeverity.Error => FAInfoBarSeverity.Error,
            _ => FAInfoBarSeverity.Informational
        };

        /// <summary>一条在场通知的内部状态。</summary>
        private sealed class Snack
        {
            public FAInfoBar Bar { get; init; } = null!;
            public string Key { get; init; } = string.Empty;
            public DispatcherTimer? Timer { get; set; }
            public bool Closing { get; set; }
            public NotificationSeverity Severity { get; init; }
        }
    }
}
