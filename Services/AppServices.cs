using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 应用级服务的静态访问点。
    ///
    /// 页面 ViewModel 数量多、构造时机分散，若每个都注入对话框 / 通知服务，
    /// 构造函数签名会急剧膨胀。这里提供轻量的服务定位，供 ViewModel 直接取用；
    /// 服务实例在 App 启动时装配。
    /// </summary>
    public static class AppServices
    {
        /// <summary>对话框服务（确认框 / 提示框）。</summary>
        public static IDialogService? Dialogs { get; set; }

        /// <summary>轻提示服务（Snackbar）。</summary>
        public static INotificationService? Notifications { get; set; }

        /// <summary>文件 / 文件夹选择服务。</summary>
        public static IFilePickerService? FilePicker { get; set; }

        /// <summary>依赖注入容器（用于按类型解析页面实例）。</summary>
        public static IServiceProvider? Provider { get; set; }
    }

    /// <summary>
    /// 应用生命周期帮助方法。
    /// </summary>
    public static class AppLifetime
    {
        /// <summary>请求关闭应用。</summary>
        public static void Shutdown()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }

        /// <summary>主窗口；未就绪时为 null。</summary>
        public static Window? MainWindow =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        /// <summary>
        /// 适合作为模态对话框 owner 的窗口。
        ///
        /// 不能一概用 <see cref="MainWindow"/>：首次运行的开机向导期间主窗口是 **Hide 状态**，
        /// 拿一个没有平台实现的窗口当 owner，Avalonia 会报
        /// <c>[Control] PlatformImpl is null, couldn't handle input</c>。
        /// 这里按「当前激活的窗口 → 第一个可见窗口 → 主窗口」依次回退。
        /// </summary>
        public static Window? DialogOwner
        {
            get
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                    return null;

                var windows = desktop.Windows;
                if (windows is null || windows.Count == 0)
                    return desktop.MainWindow;

                return windows.FirstOrDefault(w => w.IsActive)
                       ?? windows.FirstOrDefault(w => w.IsVisible)
                       ?? desktop.MainWindow;
            }
        }

        /// <summary>
        /// 在合适的宿主窗口上以模态方式显示对话框。
        /// 找不到可用 owner（或 owner 就是对话框自己）时退化为普通显示，避免直接抛异常。
        /// </summary>
        public static async Task ShowDialogAsync(Window dialog)
        {
            var owner = DialogOwner;
            if (owner is not null && !ReferenceEquals(owner, dialog))
                await dialog.ShowDialog(owner);
            else
                dialog.Show();
        }
    }
}
