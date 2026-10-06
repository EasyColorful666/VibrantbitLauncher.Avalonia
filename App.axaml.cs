using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.Views.Windows;
using VibrantbitLauncher.ViewModels.Windows;

namespace VibrantbitLauncher;

public partial class App : Application
{
    private static IHost? _host;

    /// <summary>依赖注入容器。</summary>
    public static IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("容器尚未初始化");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        if (Design.IsDesignMode)
            RequestedThemeVariant = ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // ① 先把日志系统立起来：关键是让后面每一步失败都留得下证据
            LogService.Initialize(
                SettingsService.Current.LogLevel,
                SettingsService.Current.LogRetentionDays,
                SettingsService.Current.LogToDebugOutput);

            // ② 先读配置再建窗口：页面初始化会读写 SettingsService.Current
            SettingsService.Load();

            // ③ 按用户配置重建日志
            LogService.Reinitialize();
            LogService.WriteStartupBanner(GetDisplayVersion());

            // ④ 构建依赖注入容器
            _host = BuildHost();
            _host.Start();
            AppServices.Provider = Services;

            // ⑤ 建主窗口，装配对话框 / 通知 / 文件选择服务
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            AppServices.Dialogs = new DialogService();
            AppServices.Notifications = new NotificationService(mainWindow.NotificationHost);
            AppServices.FilePicker = new FilePickerService();

            // ⑥ 外观必须在窗口建好之后再应用
            SettingsService.ApplyAppearance();

            Log.Information("配置：Theme={Theme} IsFirstRun={First} MinecraftFolder={Folder} JavaPath={Java}",
                SettingsService.Current.Theme,
                SettingsService.Current.IsFirstRun,
                SettingsService.Current.MinecraftFolder,
                string.IsNullOrEmpty(SettingsService.Current.JavaPath) ? "(未设置)" : SettingsService.Current.JavaPath);

            // ⑦ MinecraftLaunch 下载参数
            MinecraftLaunch.InitializeHelper.Initialize(settings =>
            {
                settings.MaxThread = 256;
                settings.MaxFragment = 128;
                settings.MaxRetryCount = 4;
                settings.IsEnableMirror = false;
                settings.IsEnableFragment = false;
            });

            // ⑧ 后台预热版本清单，避免首次进入「下载中心」要等几秒
            MinecraftVersionCache.Preload();

            // ⑨ 修复版本 JSON 的时区格式（几十次文件读写，放后台）
            _ = Task.Run(() => FixVersionJsonDateTimeFormat(SettingsService.Current.MinecraftFolder));

            desktop.Exit += (_, _) =>
            {
                _host?.StopAsync().GetAwaiter().GetResult();
                _host?.Dispose();
                Log.CloseAndFlush();
            };

            // ⑩ 首次运行：先跑开机向导，向导结束（关闭）后再留在主界面
            if (SettingsService.Current.IsFirstRun)
            {
                // 只挂一次：向导关闭时会把主窗口 Show 回来，而 Hide→Show 会**再次触发 Opened**；
                // 若不取消订阅就会创建第二个向导，DI 单例的向导页面二次挂载会直接抛异常。
                void OnMainWindowOpenedOnce(object? sender, EventArgs e)
                {
                    mainWindow.Opened -= OnMainWindowOpenedOnce;
                    ShowWelcomeWizard(desktop, mainWindow);
                }

                mainWindow.Opened += OnMainWindowOpenedOnce;
            }
            // ⑪ 临时冒烟（VBL_SMOKE=1）
            if (Environment.GetEnvironmentVariable("VBL_SMOKE") == "1")
                SmokeTest.Run(mainWindow);

        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 首次启动时弹出开机向导。
    ///
    /// 向导以主窗口为 owner 显示：关闭子窗口不会连带结束应用，
    /// 而主窗口始终是 desktop.MainWindow，避免向导关闭触发退出。
    /// 向导走完或用户直接关掉，都视为「已完成」，写回 IsFirstRun=false 以免下次重复弹出。
    /// </summary>
    private static void ShowWelcomeWizard(IClassicDesktopStyleApplicationLifetime desktop, MainWindow mainWindow)
    {
        // 向导期间先隐藏主窗口：走的是平台层的 Hide，不会触发 Closed，
        // 也不会让生命周期判定「窗口已关闭」而提前退出进程。
        mainWindow.Hide();

        var welcome = new WelcomeWindow();

        // 兜底：万一 Hide 之后生命周期真的判定为退出，这里把它拉回来并重新显示主窗口
        desktop.ShutdownRequested += (_, e) =>
        {
            if (SettingsService.Current.IsFirstRun)
                e.Cancel = true;
        };

        welcome.Closed += (_, _) =>
        {
            SettingsService.Current.IsFirstRun = false;
            SettingsService.Save();
            Log.Information("开机向导结束，已标记 IsFirstRun=false");

            mainWindow.Show();
            mainWindow.Activate();
        };

        // 不设 owner：owner 处于隐藏状态时，部分平台会连带把子窗口一起藏掉。
        welcome.Show();
    }

    /// <summary>构建宿主与依赖注入容器。</summary>
    private static IHost BuildHost()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddHostedService<ApplicationHostService>();

                // 基础设施服务
                services.AddSingleton<DownloadTaskService>();
                services.AddSingleton<EasyTierService>();

                // 主窗口与视图模型
                services.AddSingleton<MainWindowViewModel>();

                // 页面（单例，导航时复用）
                services.AddSingleton<Views.Pages.LaunchPage>();
                services.AddSingleton<Views.Pages.AccountPage>();
                services.AddSingleton<Views.Pages.DownloadHubPage>();
                services.AddSingleton<Views.Pages.DownloadCenterPage>();
                services.AddSingleton<Views.Pages.MultiplayerPage>();
                services.AddSingleton<Views.Pages.SettingsPage>();
                services.AddSingleton<Views.Pages.DashboardPage>();
                services.AddSingleton<Views.Pages.InstallPage>();
                services.AddSingleton<Views.Pages.RunPage>();
                services.AddSingleton<Views.Pages.VersionManagePage>();
                services.AddSingleton<Views.Pages.DownloadPage>();
                services.AddSingleton<Views.Pages.ModPage>();
                services.AddSingleton<Views.Pages.NewsDetailPage>();

                // 欢迎流程页面
                services.AddSingleton<Views.Pages.WelcomePage>();
                services.AddSingleton<Views.Pages.WelcomeSetupPage>();
                services.AddSingleton<Views.Pages.HelloEffectPage>();
                services.AddSingleton<Views.Pages.WelcomeAccountPage>();
                services.AddSingleton<Views.Pages.WelcomeCompletePage>();

                // 页面视图模型
                services.AddSingleton<ViewModels.Pages.DashboardPageViewModel>();
                services.AddSingleton<ViewModels.Pages.AccountPageViewModel>();
                services.AddSingleton<ViewModels.Pages.DownloadPageViewModel>();
                services.AddSingleton<ViewModels.Pages.DownloadCenterViewModel>();
                services.AddSingleton<ViewModels.Pages.InstallPageViewModel>();
                services.AddSingleton<ViewModels.Pages.MultiplayerPageViewModel>();
                services.AddSingleton<ViewModels.Pages.RunPageViewModel>();
                services.AddSingleton<ViewModels.Pages.SettingsPageViewModel>();
                services.AddSingleton<ViewModels.Pages.VersionManageViewModel>();
            })
            .Build();
    }

    /// <summary>
    /// 取用于展示的版本号（形如 "1.0.5" 或 "1.0.5.1"）。优先 InformationalVersion，
    /// 并剥掉 SourceLink 附加的 "+&lt;commit&gt;" 构建元数据。
    /// </summary>
    public static string GetDisplayVersion()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];

        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
    }

    /// <summary>
    /// 扫描 .minecraft/versions 下所有 JSON，将 releaseTime 等字段的 +HHMM 时区格式修正为 +HH:MM
    /// </summary>
    private static void FixVersionJsonDateTimeFormat(string mcFolder)
    {
        try
        {
            var versionsDir = Path.Combine(mcFolder, "versions");
            if (!Directory.Exists(versionsDir)) return;

            var regex = new System.Text.RegularExpressions.Regex(@"([+-]\d{2})(\d{2})""");
            int fixedCount = 0;
            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                var dirName = Path.GetFileName(dir);
                var jsonPath = Path.Combine(dir, dirName + ".json");
                if (!File.Exists(jsonPath)) continue;

                var text = File.ReadAllText(jsonPath);
                if (regex.IsMatch(text))
                {
                    var fixedText = regex.Replace(text, "$1:$2\"");
                    File.WriteAllText(jsonPath, fixedText);
                    fixedCount++;
                    Log.Debug("修正版本 JSON 的时区格式：{Path}", jsonPath);
                }
            }

            if (fixedCount > 0)
                Log.Information("已修正 {Count} 个版本 JSON 的时区格式", fixedCount);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "修正版本 JSON 时区格式失败：{Folder}", mcFolder);
        }
    }
}
