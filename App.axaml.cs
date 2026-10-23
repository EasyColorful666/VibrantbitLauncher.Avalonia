using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using VibrantbitLauncher.Helpers;
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
            // ⓪ 环境探测（Program.Main 已跑过一次，这里幂等调用，保证设计器 / 测试路径也拿到数据）
            AppEnvironment.Probe();

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
            Log.Information("运行环境：{Env}", AppEnvironment.Summary);
            Log.Information("性能模式：{State}（来源：设置）",
                SettingsService.Current.PerformanceMode ? "开启" : "关闭");
            Log.Information("渲染后端：{Backend}",
                Program.ForceSoftwareRendering
                    ? "软件渲染（性能模式 / 低配环境 / 启动参数）"
                    : "默认（GPU 优先，自动回退）");

            // ③.5 明暗主题必须**在第一个窗口出现之前**定下来。
            //
            // 否则主窗口会先按系统主题建出来（App.axaml 里 RequestedThemeVariant="Default"），
            // 等 ApplyAppearance 再改一次 —— 那一瞬间各窗口的底色 / 文字色可能出现
            // 「一个按系统、一个按设置」的错配。后面的 ApplyAppearance 仍会完整再应用一次。
            SettingsService.ApplyTheme();

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

            // ⑥.5 兜底守卫：主题变体一旦被第三方改走，立刻按用户设置纠正回来。
            //
            // FluentAvaloniaTheme.PreferSystemTheme=True 时它会把变体改成系统主题，
            // 而本工程的窗口底色 / 遮罩是按**用户设置**手写进资源的 —— 两边一错位，
            // 就出现「浅色底 + 浅色字」的整窗发白（1.0.7.14 血案）。App.axaml 里已经把它关掉，
            // 这里再加一道运行时守卫，防止别的地方（主题包 / 系统事件）把它改回去。
            Application.Current.ActualThemeVariantChanged += (_, _) =>
            {
                var actual = ActualThemeVariant;
                if (actual != SettingsService.DesiredThemeVariant)
                {
                    Log.Warning("主题变体被外部改为 {Actual}，按设置纠正回 {Desired}",
                        actual, SettingsService.DesiredThemeVariant);
                    SettingsService.ApplyTheme();
                }
            };

            Log.Information("配置：Theme={Theme} IsFirstRun={First} MinecraftFolder={Folder} JavaPath={Java}",
                SettingsService.Current.Theme,
                SettingsService.Current.IsFirstRun,
                SettingsService.Current.MinecraftFolder,
                string.IsNullOrEmpty(SettingsService.Current.JavaPath) ? "(未设置)" : SettingsService.Current.JavaPath);

            // ⑦⑧⑨ 统统推迟到主窗口「真的显示出来」之后。
            //
            // 原因：<c>desktop.MainWindow = mainWindow</c> 之后，窗口要到
            // <c>OnFrameworkInitializationCompleted</c> 返回时才由平台真正显示。
            // 在此之前做的任何磁盘 / 网络准备工作，都会原样变成用户眼前的一段白屏。
            // 这三步都不影响首帧（页面导航在 MainWindow.OnOpened 里才发生），
            // 放在 Opened 之后跑，用户看到的是「界面先出来、内容随后填充」。
            void OnMainWindowOpened(object? sender, EventArgs e)
            {
                mainWindow.Opened -= OnMainWindowOpened;
                RunPostShowStartupWork();
            }

            mainWindow.Opened += OnMainWindowOpened;

            desktop.Exit += (_, _) =>
            {
                // 退出前记一笔：这是「上一次运行是正常结束的」的唯一凭据，
                // 没有它，下次启动会把这次的正常退出误判成崩溃（见 StartupTrace）。
                StartupTrace.MarkExit("lifetime");

                // ⚠️ 这里的 StopAsync **必须带超时**。
                //
                // 这是 UI 线程上的同步等待：只要某个 hosted service 的 StopAsync
                // 需要回到 UI 线程（或者干脆卡住），进程就再也不退出了。
                // 进程不退 = 单文件自解压出来的 %TEMP%\.net\…\*.dll 一直被占用，
                // 用户下次启动就会「双击毫无反应」—— 这正是 2026-10-21
                // 「只能运行一次」的放大器。宁可少做一点收尾，也绝不能赖在后台。
                try
                {
                    if (_host?.StopAsync(TimeSpan.FromSeconds(2)).Wait(TimeSpan.FromSeconds(3)) != true)
                        Log.Warning("宿主停止超时（3s），强制继续退出");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "宿主停止时出错，忽略并继续退出");
                }

                try { _host?.Dispose(); }
                catch (Exception ex) { Log.Warning(ex, "宿主释放时出错，忽略"); }

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
            else if (ShouldOfferPerformanceMode())
            {
                // 非首次运行 + 机器看起来吃力 + 还没问过 → 问一次要不要开性能模式
                void OnMainWindowOpenedForPerf(object? sender, EventArgs e)
                {
                    mainWindow.Opened -= OnMainWindowOpenedForPerf;
                    _ = PromptPerformanceModeAsync(mainWindow);
                }

                mainWindow.Opened += OnMainWindowOpenedForPerf;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }

    /// <summary>
    /// 是否需要询问用户开启性能模式：机器探测为低配、当前没开、且历史没问过。
    /// </summary>
    private static bool ShouldOfferPerformanceMode()
        => AppEnvironment.IsLowEndHardware
           && !SettingsService.Current.PerformanceMode
           && !SettingsService.Current.PerformanceModeAsked;

    /// <summary>
    /// 低配机器上的一次性询问。
    ///
    /// 放在主窗口打开之后再弹：此时界面已经能用了，用户读完提示点一下就好，
    /// 不会像启动前弹窗那样「窗口还没出来先卡一个对话框」，观感更稳。
    /// 无论用户选什么都不再问第二次（写回 PerformanceModeAsked）。
    /// </summary>
    private static async Task PromptPerformanceModeAsync(MainWindow mainWindow)
    {
        try
        {
            var dialogs = AppServices.Dialogs;
            if (dialogs is null)
                return;

            // 让窗口先把首帧画出来再弹，避免和窗口入场动画撞在一起
            await Task.Delay(600);

            var opened = await dialogs.ConfirmAsync(
                "检测到性能优化建议",
                $"检测到你的电脑配置较低（{AppEnvironment.Summary}）。\n\n" +
                "是否开启「性能模式」？开启后将会关闭云母材质、壁纸、动效与阴影，" +
                "并改用软件渲染，界面会明显更流畅（观感会朴素一些）。\n\n" +
                "之后随时可以在「设置 → 个性化」里改回来。",
                "开启性能模式",
                "暂不开启");

            SettingsService.Current.PerformanceModeAsked = true;
            SettingsService.Current.PerformanceMode = opened;
            SettingsService.Save();

            Log.Information("性能模式询问结果：{Result}", opened ? "已开启" : "用户拒绝");

            if (opened)
            {
                SettingsService.ApplyPerformanceMode();
                SettingsService.ApplyBackground();

                // 提示用户重启才能真正切换到软件渲染（渲染后端在 AppBuilder 阶段就定了）
                await dialogs.ShowInfoAsync(
                    "已开启性能模式",
                    "性能模式已生效，「设置 → 个性化」里可以随时关闭。\n\n" +
                    "如果希望连渲染方式也一起切换（老显卡上效果更明显），请重启一次启动器。");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "性能模式提示失败");
        }
    }

    /// <summary>
    /// 主窗口显示之后才做的启动准备工作（原第 ⑦⑧⑨ 步）。
    ///
    /// 这里全是磁盘 / 网络开销，用户不需要等它们就能开始操作界面；
    /// 唯一要求是必须在页面导航之前完成 —— 而页面导航就在 MainWindow.OnOpened 里，
    /// 与本方法是同一个事件的相邻两步，因此顺序成立。
    /// </summary>
    private static void RunPostShowStartupWork()
    {
        // ⑥.6 到这里才算「启动成功」：窗口已经真的显示出来了。
        //     这条 SHOWN 是 StartupTrace 判定「上次是不是崩了」的依据，
        //     也是自动降级（下次改用软件渲染）的复位点。
        StartupTrace.MarkShown();

        // 上一次启动死在 UI 出来之前 —— 本次已经自动切了软件渲染。
        // 必须让用户知道，否则他会以为是「版本更新后画面变粗糙了」。
        if (StartupTrace.LastFailures > 0)
        {
            Log.Warning("上一次启动未完成（连续 {Count} 次），本次已自动改用软件渲染", StartupTrace.LastFailures);

            _ = Task.Run(async () =>
            {
                try
                {
                    // 等入场动画和首屏内容稳定再提示，别和飞入动画抢注意力
                    await Task.Delay(1600);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        AppServices.Notifications?.Show(
                            "上次启动没有成功，已自动切换为软件渲染。若显示正常可忽略；" +
                            "如需恢复显卡加速，请在「设置 → 个性化」里关闭性能模式后重启。",
                            "已启用兼容渲染"));
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "自动降级提示发送失败（不影响启动）");
                }
            });
        }

        // ⑦ 下载参数（下载源 / 重试次数来自用户设置，设置页改动后同一入口重新应用）
        SettingsService.ApplyDownloadSettings();

        // ⑧ 后台预热版本清单，避免首次进入「下载中心」要等几秒。
        //    性能模式下跳过：这一步要起线程 + 拉 HTTPS + 反序列化上万个条目，
        //    在单核 / 32 位进程仅有 2GB 用户态地址空间的机器上代价明显。
        if (SettingsService.Current.PerformanceMode)
            Log.Information("性能模式：跳过版本清单预热");
        else
            MinecraftVersionCache.Preload();

        // ⑨ 规范化已装版本的 JSON：修时区格式 + 给 1.13 以前的版本补 arguments。
        //    几十次文件读写，放后台；性能模式下再延后 3 秒，避开窗口首帧的资源竞争。
        var normalizeDelay = SettingsService.Current.PerformanceMode ? 3000 : 0;
        _ = Task.Run(async () =>
        {
            if (normalizeDelay > 0)
                await Task.Delay(normalizeDelay);

            NormalizeVersionJsons(SettingsService.Current.MinecraftFolder);
        });

        // ⑩ 启动 1.3 秒后自动刷新一次下载页数据（社区资源列表 + Minecraft 版本列表）。
        //
        //    为什么延后 1.3 秒：启动瞬间要抢的是首帧、配置加载和版本清单预热，
        //    这时候再压一批 HTTPS 请求只会让界面更迟钝。等 1.3 秒后再发，
        //    用户从启动页走到下载中心时数据往往已经就绪。
        //    已经创建出来的下载页会收到广播并各自刷新；页面还没创建也没关系 ——
        //    每次进入下载中心时还有一次刷新兜底（见 DownloadHubPage）。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartupRefreshDelayMs);

                // ⚠️ 顺序很重要：**先广播、再做版本清单预热**。
                // 反过来写的话，弱网下 EnumerableMinecraftAsync 要重试好几轮（实测能把这一句
                // 拖到十几秒之后），页面刷新就跟着一起被拖住了。刷新要的是「到点就发」。
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default
                        .Send<Models.RefreshDownloadDataMessage, string>(
                            new Models.RefreshDownloadDataMessage(), "RefreshDownloadData");

                    Log.Information("启动自动刷新（延迟 {Delay}ms）：已请求刷新下载页数据（社区资源 + 版本列表）",
                        StartupRefreshDelayMs);
                });

                // 顺手把版本清单催熟一次：即便广播没人接收（下载中心还没打开），
                // 之后第一次进入下载中心时也能命中缓存、秒出版本列表。
                // 放广播之后、不 await 在前，避免它拖慢刷新。
                try { await MinecraftVersionCache.GetAsync(); }
                catch (Exception ex) { Log.Debug(ex, "启动刷新：版本清单预热失败（不影响后续重试）"); }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "启动自动刷新下载页数据失败");
            }
        });
    }

    /// <summary>启动后自动刷新下载页数据的延迟（毫秒）。</summary>
    private const int StartupRefreshDelayMs = 1300;

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
    /// 扫描 .minecraft/versions 下所有 JSON 做一遍规范化：
    /// ① releaseTime 等字段的 +HHMM 时区写法修正为 +HH:MM（System.Text.Json 解析不了前者）；
    /// ② 1.13 以前的版本补上库解析器必需的空 arguments 对象 —— 缺了它，
    ///    MinecraftParser 会抛 InvalidOperationException，整个版本列表 / 启动都会挂。
    /// 详见 <see cref="VersionJsonNormalizer"/>。
    /// </summary>
    private static void NormalizeVersionJsons(string mcFolder)
    {
        try
        {
            var versionsDir = Path.Combine(mcFolder, "versions");
            if (!Directory.Exists(versionsDir)) return;

            int fixedCount = 0;
            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                var dirName = Path.GetFileName(dir);
                var jsonPath = Path.Combine(dir, dirName + ".json");
                if (!File.Exists(jsonPath)) continue;

                if (VersionJsonNormalizer.NormalizeFile(jsonPath))
                {
                    fixedCount++;
                    Log.Debug("已规范化版本 JSON：{Path}", jsonPath);
                }
            }

            if (fixedCount > 0)
                Log.Information("已规范化 {Count} 个版本 JSON", fixedCount);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "规范化版本 JSON 失败：{Folder}", mcFolder);
        }
    }
}
