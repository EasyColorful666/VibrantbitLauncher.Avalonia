using Avalonia.Controls;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 应用级对话框服务：替代 WPF 的 MessageBox / Wpf.Ui 的 MessageBox / ContentDialog。
    /// 由主窗口提供宿主，页面通过它弹确认框、提示框。
    /// </summary>
    public interface IDialogService
    {
        /// <summary>显示一个信息提示框。</summary>
        Task ShowInfoAsync(string title, string message);

        /// <summary>显示一个错误提示框。</summary>
        Task ShowErrorAsync(string title, string message);

        /// <summary>显示「确认 / 取消」，返回用户是否点了确认。</summary>
        Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", string cancelText = "取消");

        /// <summary>显示一个自定义内容对话框，返回主按钮点击结果。</summary>
        Task<bool> ShowContentAsync(string title, Control content, string primaryText = "确定", string? closeText = "取消");
    }

    /// <summary>
    /// 通知服务：替代 Wpf.Ui 的 ISnackbarService，用于右下角轻提示。
    /// </summary>
    public interface INotificationService
    {
        /// <summary>弹出一条轻提示。</summary>
        void Show(string message, string? title = null, NotificationSeverity severity = NotificationSeverity.Informational);

        /// <summary>弹出一条成功提示。</summary>
        void Success(string message, string? title = null);

        /// <summary>弹出一条警告提示。</summary>
        void Warning(string message, string? title = null);

        /// <summary>弹出一条错误提示。</summary>
        void Error(string message, string? title = null);
    }

    public enum NotificationSeverity
    {
        Informational,
        Success,
        Warning,
        Error
    }
}
