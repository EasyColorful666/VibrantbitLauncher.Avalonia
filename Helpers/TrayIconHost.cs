using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Serilog;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 系统托盘图标。
///
/// <para>
/// 解决的是「启动器关掉就得重新打开」的问题：托盘常驻之后，点窗口右上角的 × 只是收进托盘，
/// 联机内核（EasyTier）仍在后台跑，房客不会掉线；真正的退出走托盘菜单里的「退出启动器」。
/// </para>
///
/// <para>
/// 菜单里的「关闭窗口时最小化到托盘」直接读写 <see cref="SettingsService.Current"/>，
/// 不额外占一个设置页位置 —— 这个开关本身就是给人「想关的时候随手关」用的。
/// </para>
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    /// <summary>图标资源地址（csproj 里 Assets\** 已声明为 AvaloniaResource）。</summary>
    private const string IconUri = "avares://VibrantbitLauncher.Avalonia/Assets/icon.ico";

    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _statusItem;
    private readonly NativeMenuItem _trayToggle;
    private bool _disposed;

    /// <param name="showWindow">把主窗口重新叫出来。</param>
    /// <param name="stopMultiplayer">断掉虚拟网络（托盘里直接停内核，不必先开窗口）。</param>
    /// <param name="showToolbox">唤出游戏内工具箱浮窗（热键被占用时的备用入口）。</param>
    /// <param name="exit">真正退出整个程序。</param>
    public TrayIconHost(Action showWindow, Action stopMultiplayer, Action showToolbox, Action exit)
    {
        ArgumentNullException.ThrowIfNull(showWindow);
        ArgumentNullException.ThrowIfNull(stopMultiplayer);
        ArgumentNullException.ThrowIfNull(showToolbox);
        ArgumentNullException.ThrowIfNull(exit);

        // 状态行只是展示，不可点。文案由 SetStatus 更新（联机启动 / 停止 / 接管都会刷）。
        _statusItem = new NativeMenuItem("联机：未启动") { IsEnabled = false };

        var showItem = new NativeMenuItem("显示主界面");
        showItem.Click += (_, _) => showWindow();

        // 热键是「游戏运行时才注册」的，被别的软件占用时就没有任何入口了 ——
        // 托盘这一项是那种情况下的兜底（也方便想先开着窗口再进游戏的人）。
        var toolboxItem = new NativeMenuItem("Minecraft 工具箱");
        toolboxItem.Click += (_, _) => showToolbox();

        // 托盘是唯一能看到「后台还有内核在跑」的地方，所以断开入口也必须在这里 ——
        // 否则用户想停掉后台联机只能再打开启动器走一遍联机页。
        var stopItem = new NativeMenuItem("断开联机（停止虚拟网络）");
        stopItem.Click += (_, _) => stopMultiplayer();

        _trayToggle = new NativeMenuItem("关闭窗口时最小化到托盘")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = SettingsService.Current.MinimizeToTray
        };

        _trayToggle.Click += (_, _) =>
        {
            SettingsService.Current.MinimizeToTray = _trayToggle.IsChecked;
            SettingsService.Save();
            Log.Information("托盘菜单：关闭窗口时最小化到托盘 = {Value}", _trayToggle.IsChecked);
        };

        var exitItem = new NativeMenuItem("退出启动器");
        exitItem.Click += (_, _) => exit();

        var menu = new NativeMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(showItem);
        menu.Items.Add(toolboxItem);
        menu.Items.Add(stopItem);
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _tray = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "Vibrantbit Launcher",
            Menu = menu,
            IsVisible = true
        };

        // 左键单击 = 叫回窗口；右键由平台按 Menu 自行弹出。
        _tray.Clicked += (_, _) => showWindow();

        // Avalonia 12 走「Application 上的 TrayIcon.Icons 集合」注册，
        // 直接把实例 new 出来是不会出现在任务栏通知区域的。
        var application = Application.Current;
        if (application is null)
            throw new InvalidOperationException("托盘图标必须在 Application 初始化之后创建");

        var icons = TrayIcon.GetIcons(application) ?? new TrayIcons();
        icons.Add(_tray);
        TrayIcon.SetIcons(application, icons);

        Log.Information("系统托盘图标已创建");
    }

    /// <summary>更新托盘状态文案（同时作用于 tooltip 与菜单第一行）。</summary>
    public void SetStatus(string text)
    {
        if (_disposed)
            return;

        _statusItem.Header = text;

        // Windows 的通知区域 tooltip 实际只显示一行，这里保持单行可读。
        _tray.ToolTipText = string.IsNullOrWhiteSpace(text)
            ? "Vibrantbit Launcher"
            : $"Vibrantbit Launcher — {text}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _tray.IsVisible = false;

            var application = Application.Current;
            if (application is not null)
            {
                var icons = TrayIcon.GetIcons(application);
                icons?.Remove(_tray);
            }

            _tray.Dispose();
        }
        catch (Exception ex)
        {
            // 退出路径上的异常绝不能冒泡：托盘收不掉顶多是图标残留，崩在这里就是「关不掉」。
            Log.Debug(ex, "释放托盘图标失败");
        }
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(IconUri));
            return new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            // 图标丢了不该拦住托盘本身（菜单仍然可用），只是通知区域里看不到图。
            Log.Warning(ex, "托盘图标资源加载失败：{Uri}", IconUri);
            return null;
        }
    }
}
