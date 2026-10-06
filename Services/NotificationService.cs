using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 轻提示服务：在窗口右下角弹出一条 InfoBar，若干秒后自动消失。
    /// 替代 Wpf.Ui 的 ISnackbarService。
    /// </summary>
    public sealed class NotificationService : INotificationService
    {
        private readonly Panel _host;
        private const int MaxVisible = 4;

        public NotificationService(Panel host)
        {
            _host = host;
        }

        public void Show(string message, string? title = null, NotificationSeverity severity = NotificationSeverity.Informational)
        {
            void Post()
            {
                var bar = new FAInfoBar
                {
                    Message = message,
                    Title = title ?? string.Empty,
                    Severity = Map(severity),
                    IsOpen = true,
                    IsClosable = true,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MinWidth = 280,
                    MaxWidth = 420,
                    Margin = new Avalonia.Thickness(0, 0, 16, 8)
                };

                // 超出上限时清掉最旧的，避免长时间运行后堆积
                while (_host.Children.Count >= MaxVisible)
                    _host.Children.RemoveAt(0);

                _host.Children.Add(bar);

                // 自动关闭：InfoBar 是轻提示形态，不应要求用户手动关闭
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(severity == NotificationSeverity.Error ? 6 : 4) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    _host.Children.Remove(bar);
                };
                timer.Start();
            }

            if (Dispatcher.UIThread.CheckAccess())
                Post();
            else
                Dispatcher.UIThread.Post(Post);
        }

        public void Success(string message, string? title = null) => Show(message, title ?? "成功", NotificationSeverity.Success);

        public void Warning(string message, string? title = null) => Show(message, title ?? "提示", NotificationSeverity.Warning);

        public void Error(string message, string? title = null) => Show(message, title ?? "错误", NotificationSeverity.Error);

        private static FAInfoBarSeverity Map(NotificationSeverity severity) => severity switch
        {
            NotificationSeverity.Success => FAInfoBarSeverity.Success,
            NotificationSeverity.Warning => FAInfoBarSeverity.Warning,
            NotificationSeverity.Error => FAInfoBarSeverity.Error,
            _ => FAInfoBarSeverity.Informational
        };
    }
}
