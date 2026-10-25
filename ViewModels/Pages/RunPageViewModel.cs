using VibrantbitLauncher.Helpers;
using CommunityToolkit.Mvvm.Messaging;
using MinecraftLaunch.Base.Models.Authentication;
using MinecraftLaunch.Base.Models.Game;
using MinecraftLaunch.Components.Authenticator;
using MinecraftLaunch.Components.Logging;
using MinecraftLaunch.Extensions;
using MinecraftLaunch.Utilities;
using VibrantbitLauncher.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using VibrantbitLauncher.ViewModels.Windows;
using VibrantbitLauncher.Views.Pages;
using VibrantbitLauncher.Views.Windows;
using System.Threading.Tasks;
namespace VibrantbitLauncher.ViewModels.Pages
{
    public partial class RunPageViewModel : ObservableObject
    {
        // Java 列表由 LoadAsync() 在后台线程填充。
        // 原实现在字段初始化器里同步枚举系统 Java（JavaUtil.EnumerableJavaAsync().ToBlockingEnumerable()），
        // 会在候选路径为空时抛 ArgumentException，并把 UI 线程卡住；构造函数里不能做同步 IO。
        private List<JavaEntry> asyncJavas = new();

        /// <summary>
        /// 机器上**每一个**可用 Java（不按主版本合并），只给「用户自己挑」的下拉框看。
        ///
        /// <para>
        /// 和 <see cref="asyncJavas"/> 分开是因为两者用途不同：自动匹配只要「每个主版本一个」
        /// （多的只会干扰 GetAppropriateJava），而玩家手动选择时要看到全部安装 ——
        /// 同一台机器装 jdk-17 / jdk-21 / jdk-25 三份是常态，合并掉就选不到想要的那个。
        /// </para>
        /// </summary>
        private List<JavaEntry> allJavas = new();

        private ObservableCollection<MinecraftEntry> minecrafts = new ObservableCollection<MinecraftEntry>();
        private ObservableCollection<LocalVersion> minecraftVersions = new ObservableCollection<LocalVersion>();
        private ObservableCollection<string> javaVersions = new ObservableCollection<string>();
        private MinecraftRunner runner;
        private readonly INotificationService _notification = AppServices.Notifications!;
        string selectedVersion = "";
        private string accountName;
        private bool isRefreshing;

        public bool IsLoaded { get; set; }
        public RelayCommand LoadCommand { get; set; }
        public RelayCommand RefreshCommand { get; set; }

        /// <summary>
        /// 是否正在扫描本地版本。
        ///
        /// <para>
        /// 启动页要靠它区分「还在扫盘」与「真的一个版本都没有」：
        /// <see cref="Refresh"/> 是 fire-and-forget 的，页面 Loaded 时集合必然还是空的，
        /// 若只看 <c>MinecraftVersions.Count</c> 就会先闪一下「还没有可用的游戏版本」。
        /// </para>
        /// </summary>
        public bool IsRefreshing
        {
            get => isRefreshing;
            private set => SetProperty(ref isRefreshing, value);
        }

        public ObservableCollection<LocalVersion> MinecraftVersions
        {
            get => minecraftVersions;
            set => SetProperty(ref minecraftVersions, value);
        }
        public string SelectedVersion
        {
            get => selectedVersion;
            set => SetProperty(ref selectedVersion, value);
        }
        public string AccountName
        {
            get => accountName;
            set => SetProperty(ref accountName, value);
        }

        /// <summary>
        /// 已检测到的 Java 列表（启动页「快捷设置」的 Java 下拉框用）。
        ///
        /// <para>
        /// 这里是**全部安装**，不是「每个主版本一个」—— 下拉框的用途就是让玩家挑，
        /// 合并过的列表会出现「明明装了 21，列表里只有 21.0.3」的情况，选不到自己那份。
        /// 启动时自动匹配用的是另一份合并列表（<c>asyncJavas</c>）。
        /// </para>
        /// </summary>
        public IReadOnlyList<JavaEntry> DetectedJavas => allJavas;

        // ===== 主页模块（问候语 / 启动卡 / 最近游玩 / 实例概览 / 快捷入口） =====
        //
        // 这些开关都在「设置 → 通用 → 主页」里，页面本身只负责按开关显示或隐藏。
        // 每次导航进来都要重新读一遍配置：用户可能刚在设置里关掉某一项。

        public bool ShowGreeting => SettingsService.Current.ShowHomeGreeting;
        public bool ShowRecent => SettingsService.Current.ShowHomeRecent;
        public bool ShowInstanceOverview => SettingsService.Current.ShowHomeInstanceOverview;
        public bool ShowShortcuts => SettingsService.Current.ShowHomeShortcuts;

        private bool showLaunchCard = true;

        /// <summary>是否显示「游戏卡片」（版本列表 + 启动按钮）。开关在本页的快捷设置里。</summary>
        public bool ShowLaunchCard
        {
            get => showLaunchCard;
            set
            {
                if (!SetProperty(ref showLaunchCard, value))
                    return;

                SettingsService.Current.ShowHomeLaunchCard = value;
                SettingsService.Save();
            }
        }

        private string greetingText = string.Empty;
        private string instanceCountText = string.Empty;
        private string instanceSizeText = string.Empty;

        /// <summary>问候语（按时间段 + 当前账户）。</summary>
        public string GreetingText
        {
            get => greetingText;
            private set => SetProperty(ref greetingText, value);
        }

        public string InstanceCountText
        {
            get => instanceCountText;
            private set => SetProperty(ref instanceCountText, value);
        }

        public string InstanceSizeText
        {
            get => instanceSizeText;
            private set => SetProperty(ref instanceSizeText, value);
        }

        /// <summary>最近玩过的几个实例（按版本目录的修改时间排）。</summary>
        public ObservableCollection<RecentInstance> RecentInstances { get; } = new();

        public bool HasRecent => RecentInstances.Count > 0;

        public RelayCommand<string> SelectRecentCommand { get; }
        public RelayCommand OpenModDownloadCommand { get; }
        public RelayCommand OpenModpackDownloadCommand { get; }

        /// <summary>重新计算首页四个模块要用的数据。刷新版本列表之后调用。</summary>
        private void RefreshHomeSections()
        {
            ShowLaunchCard = SettingsService.Current.ShowHomeLaunchCard;

            OnPropertyChanged(nameof(ShowGreeting));
            OnPropertyChanged(nameof(ShowRecent));
            OnPropertyChanged(nameof(ShowInstanceOverview));
            OnPropertyChanged(nameof(ShowShortcuts));

            GreetingText = BuildGreeting();
            InstanceCountText = $"{MinecraftVersions.Count} 个实例";

            var root = SettingsService.ResolveMinecraftFolder();
            var versionsDir = Path.Combine(root, "versions");

            RecentInstances.Clear();
            foreach (var item in MinecraftVersions
                         .Select(v => (Id: v.Version, Time: SafeLastWrite(Path.Combine(versionsDir, v.Version))))
                         .OrderByDescending(t => t.Time)
                         .Take(3))
            {
                RecentInstances.Add(new RecentInstance
                {
                    Id = item.Id,
                    TimeText = RelativeTime(item.Time),
                    Command = SelectRecentCommand
                });
            }

            OnPropertyChanged(nameof(HasRecent));

            if (!SettingsService.Current.ShowHomeInstanceOverview)
                return;

            // 占用空间要遍历几万个文件，绝不能放在 UI 线程上算
            InstanceSizeText = "正在统计…";
            _ = Task.Run(() =>
            {
                var bytes = SumDirectory(root, maxDepth: 4, skipNames: new[] { "logs", "crash-reports" });
                var text = bytes <= 0 ? "—" : FormatSize(bytes);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => InstanceSizeText = $"占用 {text}");
            });
        }

        private static string BuildGreeting()
        {
            var hour = DateTime.Now.Hour;
            var period = hour switch
            {
                < 6 => "凌晨好",
                < 11 => "早上好",
                < 14 => "中午好",
                < 18 => "下午好",
                _ => "晚上好"
            };

            var name = MainWindowViewModel.MainModel.Account?.Name;
            return string.IsNullOrWhiteSpace(name)
                ? $"{period}，先登录一个账户再开始"
                : $"{period}，{name}";
        }

        private static DateTime SafeLastWrite(string path)
        {
            try
            {
                return Directory.Exists(path) ? Directory.GetLastWriteTime(path) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static string RelativeTime(DateTime time)
        {
            if (time == DateTime.MinValue)
                return "从未启动";

            var span = DateTime.Now - time;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalHours < 1) return $"{span.TotalMinutes:0} 分钟前";
            if (span.TotalDays < 1) return $"{span.TotalHours:0} 小时前";
            if (span.TotalDays < 30) return $"{span.TotalDays:0} 天前";
            return time.ToString("yyyy-MM-dd");
        }

        /// <summary>
        /// 粗略统计目录占用。限制深度并跳过日志目录 —— 目的是「大概占了多少」，
        /// 精确到字节没有意义，扫太久反而会让首页看起来卡住。
        /// </summary>
        private static long SumDirectory(string path, int maxDepth, IReadOnlyCollection<string> skipNames)
        {
            try
            {
                if (!Directory.Exists(path))
                    return 0;

                long total = 0;
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    try { total += new FileInfo(file).Length; } catch { /* 被占用 / 无权限 */ }
                }

                if (maxDepth <= 1)
                    return total;

                foreach (var dir in Directory.EnumerateDirectories(path))
                {
                    var name = Path.GetFileName(dir);
                    if (skipNames.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    total += SumDirectory(dir, maxDepth - 1, skipNames);
                }

                return total;
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
            _ => $"{bytes / 1024.0 / 1024 / 1024:F1} GB"
        };

        public RunPageViewModel()
        {
            IsLoaded = false;
            LoadCommand = new RelayCommand(Load);
            RefreshCommand = new RelayCommand(Refresh);

            SelectRecentCommand = new RelayCommand<string>(id =>
            {
                if (!string.IsNullOrEmpty(id))
                    SelectedVersion = id;
            });

            OpenModDownloadCommand = new RelayCommand(() =>
                AppNavigation.Navigate(typeof(DownloadHubPage)));
            OpenModpackDownloadCommand = new RelayCommand(() =>
                AppNavigation.Navigate(typeof(DownloadHubPage)));
        }

        private static List<string> GetLocalVersionIds()
        {
            var result = new List<string>();
            var mcFolder = MainWindowViewModel.MainModel.MinecraftFolder ?? "./.minecraft";
            var versionsPath = Path.Combine(mcFolder, "versions");
            if (!Directory.Exists(versionsPath)) return result;

            var allDirs = Directory.GetDirectories(versionsPath);
            var inheritedBaseIds = new HashSet<string>();

            // 第一遍：读取所有版本 JSON，收集被其他版本继承的基础版本 ID
            foreach (var dir in allDirs)
            {
                var dirName = Path.GetFileName(dir);
                var jsonPath = Path.Combine(dir, dirName + ".json");
                if (!File.Exists(jsonPath)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
                    if (doc.RootElement.TryGetProperty("inheritsFrom", out var inheritsEl))
                    {
                        var baseId = inheritsEl.GetString();
                        if (!string.IsNullOrEmpty(baseId))
                            inheritedBaseIds.Add(baseId);
                    }
                }
                catch { }
            }

            // 第二遍：只列出不是"被继承基础版本"的版本
            foreach (var dir in allDirs)
            {
                var dirName = Path.GetFileName(dir);
                var jsonPath = Path.Combine(dir, dirName + ".json");
                if (!File.Exists(jsonPath)) continue;
                if (inheritedBaseIds.Contains(dirName)) continue;
                result.Add(dirName);
            }
            return result;
        }

        public async Task LoadAsync()
        {
            await Task.Run(async () =>
            {
                try
                {
                    // 两份列表：合并版给自动匹配，全量版给「快捷设置 → Java」下拉框
                    var all = await JavaHelper.FindAllJavasAsync();
                    var customJava = MainWindowViewModel.MainModel.JavaPath;

                    // 设置里的自定义 Java 也要在列表里（同样要过静态体检，
                    // 避免把 PATH 里的 Oracle 转发器当成可用 Java 存进列表）。
                    // 手工选过的路径多半不在扫描范围内（比如 D:\Dev\jdk），漏掉的话
                    // 下拉框会因为「当前值对不上任何一项」显示成空白 —— 看起来像没设置。
                    if (JavaHelper.IsUsableJava(customJava)
                        && !all.Any(j => string.Equals(j.JavaPath, customJava, StringComparison.OrdinalIgnoreCase)))
                    {
                        // 探一下版本号：下拉框第一行要显示 "21.0.3" 这种，否则只有路径、看不出是哪版
                        var probed = await JavaHelper.ProbeJavaAsync(customJava);
                        all.Add(probed ?? new JavaEntry { JavaPath = customJava, Is64bit = true });
                    }

                    allJavas = all;

                    // 自动匹配仍旧一份「每个主版本 + 位数留最新」的紧凑列表
                    asyncJavas = all
                        .GroupBy(j => $"{JavaHelper.MajorOf(j.JavaVersion)}|{j.Is64bit}")
                        .Select(g => g.First())
                        .ToList();
                }
                catch { }

                try
                {
                    var versionIds = GetLocalVersionIds();
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        minecraftVersions.Clear();
                        foreach (var id in versionIds)
                        {
                            minecraftVersions.Add(new LocalVersion { Version = id, RunCommand = new(RunMinecraft), ToolsCommand = new(OpenTools), SettingsCommand = new(Settings) });
                        }
                    });

                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        javaVersions.Clear();
                        foreach (var j in asyncJavas)
                        {
                            javaVersions.Add(j.JavaVersion);
                        }

                        // 启动页的「快捷设置 → Java」下拉框挂在这个属性上：
                        // 扫描是异步的，不通知的话它永远显示「未检测到」
                        OnPropertyChanged(nameof(DetectedJavas));
                    });
                }
                catch (Exception ex)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() => _notification.Error($"无法获取本地版本: {ex.Message}"));
                }
            });

            IsLoaded = true;
        }

        private int refreshing;

        private void Refresh()
        {
            // 反复点导航会让 Loaded 重复触发。已有刷新在跑就直接忽略，
            // 否则会累积多个扫盘任务并反复重建列表，表现为"点来点去越来越卡"。
            if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0)
                return;

            // 扫描期间置位，页面据此显示「正在扫描」而不是「还没有可用的游戏版本」。
            // 已在跑时上面就 return 了，所以这里可以放心地把标志拉起来。
            IsRefreshing = true;

            Task.Run(async () =>
            {
                try
                {
                    var versionIds = GetLocalVersionIds();
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        minecraftVersions.Clear();
                        foreach (var id in versionIds)
                        {
                            minecraftVersions.Add(new LocalVersion { Version = id, RunCommand = new(RunMinecraft), ToolsCommand = new(OpenTools), SettingsCommand = new(Settings) });
                        }
                    });
                }
                catch (Exception ex)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() => _notification.Error($"无法刷新本地版本: {ex.Message}"));
                }
                finally
                {
                    Interlocked.Exchange(ref refreshing, 0);
                    // 注意要在 UI 线程复位：集合的 Clear/Add 也在 UI 线程，
                    // 顺序上先填完列表、再落下「扫描结束」，页面就不会看到中间态。
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        IsRefreshing = false;
                        RefreshHomeSections();
                    });
                }
            });
        }

        private void Load()
        {
            Refresh();
        }

        void Settings(string McVersion)
        {
            if (string.IsNullOrEmpty(McVersion))
            {
                _notification.Error("请选择一个版本");
                return;
            }

            // ⚠️ 顺序必须是「先导航、再发版本号」：
            // VersionManagePage / VersionManageViewModel 在 DI 里是懒加载的单例，
            // 第一次点「设置」时它们还没被创建，也就没有注册 McVersionForManage 的接收方 ——
            // 先发消息会把版本号丢进空气里，页面随后打开时只有空目录、也不刷新。
            // 导航是同步的（MainWindow 收到 NavigateTo 立刻解析并创建页面），
            // 所以先把页面拉起来，再发版本号，接收方一定已经在。
            WeakReferenceMessenger.Default.Send<Type, string>(typeof(VersionManagePage), "NavigateTo");
            WeakReferenceMessenger.Default.Send<string, string>(McVersion, "McVersionForManage");
        }

        /// <summary>
        /// 打开实例工具箱（依赖体检 / 配装分享码）。
        /// 窗口自己会枚举实例并在下拉框里选中传入的版本，所以这里只需要把版本号带过去。
        /// </summary>
        void OpenTools(string McVersion)
        {
            if (string.IsNullOrEmpty(McVersion))
            {
                _notification.Error("请选择一个版本");
                return;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var window = new InstanceToolsWindow(McVersion);
                    await AppLifetime.ShowDialogAsync(window);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "打开实例工具箱失败");
                    _notification.Error($"无法打开工具箱：{ex.Message}");
                }
            });
        }

        private static readonly HashSet<string> launchingVersions = new();

        async void RunMinecraft(string McVersion)
        {
            if (string.IsNullOrEmpty(McVersion))
            {
                ShowError("请选择一个版本");
                return;
            }

            lock (launchingVersions)
            {
                if (launchingVersions.Contains(McVersion))
                {
                    ShowInfo($"{McVersion} 正在启动中，请稍候...");
                    return;
                }
                launchingVersions.Add(McVersion);
            }

            // 启动过程的逐步骤诊断。
            // 以前这里自己往 launch_debug.log 追加（另一份「日志系统」，散在程序目录里没人清理），
            // 现在并入统一日志：等级是 Debug，所以默认的「信息」级别下不会污染日志，
            // 需要排查「点了启动没反应」时，到设置里把等级调成「调试」再复现即可。
            static void Diag(string msg) => Serilog.Log.Debug("[启动] {Message}", msg);

            try
            {
                Diag($"=== Launch start: {McVersion} ===");

                // 用设置中的游戏目录初始化 parser
                var mcFolder = SettingsService.ResolveMinecraftFolder();
                Diag($"mcFolder: {mcFolder}");
                var parser = new MinecraftParser(mcFolder);

                MinecraftEntry selectedMinecraftEntry;
                try
                {
                    selectedMinecraftEntry = parser.GetMinecraft(McVersion);
                    Diag($"Parsed: {selectedMinecraftEntry?.Id}, Type: {selectedMinecraftEntry?.GetType().Name}");
                }
                catch (Exception ex)
                {
                    Diag($"Parse FAILED: {ex.GetType().Name}: {ex.Message}");
                    ShowError($"解析版本失败: {ex.Message}");
                    lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                    return;
                }

                if (selectedMinecraftEntry == null)
                {
                    Diag("Entry is null");
                    ShowError($"未找到版本: {McVersion}");
                    lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                    return;
                }

                // 匹配 Java。
                //
                // 顺序：**玩家自己选的 > 自动匹配**。
                // 以前不管设置里选了什么都先跑 GetAppropriateJava（它只在候选里按 MC 版本挑
                // 「够新」的一个，完全无视用户选择），于是「快捷设置里换了 Java」看起来毫无效果，
                // 甚至会把装了 MOD 的 1.20.1 实例丢给 JDK 27 去跑 —— 老 Forge / Mixin
                // 在超高版本 JDK 上直接崩，用户看到的只是「异常退出（代码 1）」。
                JavaEntry javaPath = null;
                var configuredJava = MainWindowViewModel.MainModel.JavaPath;

                if (JavaHelper.IsUsableJava(configuredJava))
                {
                    javaPath = allJavas.FirstOrDefault(j =>
                                   string.Equals(j.JavaPath, configuredJava, StringComparison.OrdinalIgnoreCase))
                               ?? new JavaEntry { JavaPath = configuredJava, Is64bit = true };
                    Diag($"Using user-selected Java: {javaPath.JavaPath}");
                }
                else
                {
                    try
                    {
                        javaPath = selectedMinecraftEntry.GetAppropriateJava(asyncJavas);
                        Diag($"Java matched: {javaPath?.JavaPath} (version {javaPath?.JavaVersion})");
                    }
                    catch (Exception ex)
                    {
                        Diag($"GetAppropriateJava threw: {ex.Message}");
                        if (asyncJavas.Count > 0)
                        {
                            javaPath = asyncJavas.First();
                            Diag($"Fallback to first Java: {javaPath?.JavaPath}");
                        }
                        else
                        {
                            Diag("No Java found");
                            ShowError("未找到匹配的 Java 版本，请在设置中选择 Java");
                            lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                            return;
                        }
                    }
                }

                // 启动前再体检一次选中的 Java：PATH 里的 Oracle 转发器（javapath / java8path）
                // 会让 java.exe 弹出「could not find java.dll」的模态框，必须换成真正能用的那一份。
                if (!JavaHelper.IsUsableJava(javaPath?.JavaPath))
                {
                    Diag($"Java not usable: {javaPath?.JavaPath}");
                    var fallback = asyncJavas.FirstOrDefault(j => JavaHelper.IsUsableJava(j.JavaPath));
                    if (fallback == null)
                    {
                        ShowError($"Java 不可用: {javaPath?.JavaPath}（该目录下没有 java.dll，请在设置里重新选择 Java）");
                        lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                        return;
                    }

                    Diag($"Fallback to usable Java: {fallback.JavaPath}");
                    javaPath = fallback;
                }

                // 检查账户
                if (MainWindowViewModel.MainModel.Account == null)
                {
                    Diag("Account is null");
                    ShowError("请先登录一个账户");
                    lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                    return;
                }
                Diag($"Account: {MainWindowViewModel.MainModel.Account.Name}");

                // 微软账户：启动前刷新 access token
                if (MainWindowViewModel.MainModel.IsMicrosoftAccount && MainWindowViewModel.MainModel.Account is MicrosoftAccount msAccount)
                {
                    try
                    {
                        Diag("Refreshing Microsoft account token...");
                        var authenticator = new MicrosoftAuthenticator("f4c1c237-e68f-4866-a998-82f0db041c55");
                        var newProfile = await authenticator.RefreshAsync(msAccount);
                        MainWindowViewModel.MainModel.Account = newProfile;
                        SettingsService.SaveMicrosoftAccount(newProfile);
                        Diag($"Token refreshed successfully, new account: {newProfile.Name}");
                    }
                    catch (Exception ex)
                    {
                        Diag($"Token refresh failed: {ex.GetType().Name}: {ex.Message}");
                        ShowError($"令牌刷新失败，请重新登录微软账户: {ex.Message}");
                        lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                        return;
                    }
                }

                // 启动前完整性体检：client.jar / 资源索引 / 依赖库 / 资源对象。
                // 缺东西时**不再抛异常**（原来是弹一句「缺少资源索引文件…请到下载中心重新安装该版本」），
                // 改成打开「补全资源」窗口 —— 只补缺的那部分，并显示进度。
                // 检查本身是几百上千次 File.Exists，丢线程池。
                IntegrityReport? integrity = null;
                try
                {
                    // 先给一句反馈：体检要查上千个文件，机械硬盘上能感觉到一两百毫秒，
                    // 什么都不显示会让用户以为「点了启动没反应」。
                    ShowInfo("正在检查游戏文件完整性…");
                    integrity = await Task.Run(() => InstanceIntegrityChecker.Check(mcFolder, McVersion));
                }
                catch (Exception ex)
                {
                    Diag($"Integrity check failed: {ex.GetType().Name}: {ex.Message}");
                }

                if (integrity is { IsHealthy: false })
                {
                    Diag($"Integrity issues: {string.Join(" | ", integrity.Issues.Select(i => i.Title + " x" + i.Count))}");
                    lock (launchingVersions) { launchingVersions.Remove(McVersion); }

                    ShowInfo($"{integrity.Summary()}，已打开「补全资源」窗口");
                    try
                    {
                        await AppLifetime.ShowDialogAsync(new AssetRepairWindow(integrity));
                    }
                    catch (Exception ex)
                    {
                        Diag($"Show repair window failed: {ex.Message}");
                        ShowError($"无法打开补全资源窗口：{ex.Message}");
                    }

                    return;
                }

                Diag("Launching manually (bypassing MinecraftLaunch runner)...");
                ShowInfo($"正在启动 {McVersion}，请稍等...");

                // 读版本 JSON、拼 classpath、逐个检查几百个库文件是否存在 —— 全是同步文件 IO。
                // 丢到线程池执行，否则点「运行」的那一瞬间 UI 会僵住。
                var rawProcess = await Task.Run(() => LaunchMinecraftManually(mcFolder, McVersion, javaPath, MainWindowViewModel.MainModel.Account, MainWindowViewModel.MainModel.IsMicrosoftAccount, Diag));
                Diag($"Manual launch returned, process null? {rawProcess == null}");

                if (rawProcess == null)
                {
                    ShowError("启动失败：进程未创建");
                    lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                    return;
                }

                try { Diag($"Process Id: {rawProcess.Id}, HasExited: {rawProcess.HasExited}, StartTime: {rawProcess.StartTime}"); } catch (Exception ex) { Diag($"Process info error: {ex.Message}"); }

                // 捕获输出
                rawProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) { Diag($"[STDOUT] {e.Data}"); writeLog(DateTime.Now.ToString("HH:mm:ss"), e.Data); } };
                rawProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) { Diag($"[STDERR] {e.Data}"); writeLog(DateTime.Now.ToString("HH:mm:ss"), "[ERR] " + e.Data); } };
                rawProcess.BeginOutputReadLine();
                rawProcess.BeginErrorReadLine();

                // 等待 3 秒后再次检查进程状态
                await Task.Delay(3000);
                try
                {
                    bool exited = rawProcess.HasExited;
                    Diag($"After 3s - HasExited: {exited}");
                    if (exited)
                    {
                        Diag($"After 3s - ExitCode: {rawProcess.ExitCode}");
                    }
                    else
                    {
                        Diag($"After 3s - Process still running, Responding: {rawProcess.Responding}, MainWindowTitle: '{rawProcess.MainWindowTitle}'");
                    }
                }
                catch (Exception ex) { Diag($"After 3s check error: {ex.Message}"); }

                void HandleExit()
                {
                    int exitCode = -1;
                    try { exitCode = rawProcess.ExitCode; } catch { }
                    Diag($"Process exited with code {exitCode}");

                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        if (exitCode != 0)
                        {
                            ShowError($"Minecraft 异常退出（代码 {exitCode}）");
                            try
                            {
                                var window = new CrashAnalysisWindow(selectedMinecraftEntry);
                                _ = AppLifetime.ShowDialogAsync(window);
                            }
                            catch (Exception ex)
                            {
                                Diag($"CrashAnalysisWindow failed: {ex.Message}");
                            }
                        }
                        else
                        {
                            ShowInfo("Minecraft 已正常退出");
                        }
                    });
                }

                rawProcess.EnableRaisingEvents = true;
                rawProcess.Exited += (_, _) => HandleExit();

                // 防竞态：如果进程已经秒退，直接处理
                bool alreadyExited = false;
                try { alreadyExited = rawProcess.HasExited; } catch { }
                if (alreadyExited)
                {
                    Diag("Process already exited before event attached");
                    HandleExit();
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() => ShowSuccess("Minecraft 已启动"));

                    // 确认游戏真的起来了（不是秒退）再执行「启动后行为」，
                    // 否则会把用户的启动器一起关掉而游戏根本没开起来。
                    ApplyAfterLaunchAction();
                }

                Diag("=== Launch setup complete, waiting for process ===");
                lock (launchingVersions) { launchingVersions.Remove(McVersion); }
            }
            catch (InvalidOperationException ex)
            {
                Diag($"InvalidOperationException: {ex.Message}");
                ShowError("启动失败: 请登录一个用户");
                lock (launchingVersions) { launchingVersions.Remove(McVersion); }
            }
            catch (Exception ex)
            {
                Diag($"Unhandled: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                ShowError($"启动失败: {ex.Message}");
                lock (launchingVersions) { launchingVersions.Remove(McVersion); }
            }
        }

        private static Process LaunchMinecraftManually(string mcFolder, string versionId, JavaEntry java, Account account, bool forceMicrosoft, Action<string> diag)
        {
            // 转成绝对路径，避免 WorkingDirectory 与 classpath 路径不一致
            mcFolder = Path.GetFullPath(mcFolder);
            diag($"Absolute mcFolder: {mcFolder}");

            var versionJsonPath = Path.Combine(mcFolder, "versions", versionId, versionId + ".json");
            diag($"Reading version JSON: {versionJsonPath}");
            var versionJson = JsonDocument.Parse(File.ReadAllText(versionJsonPath)).RootElement;

            var libraries = new List<string>();
            string mainClass = "";
            var jvmArgs = new List<string>();
            var gameArgs = new List<string>();
            string assetsIndex = "latest";
            string parentId = null;

            // 同一个库装两个版本会让模块层直接崩：
            //   Modules joptsimple and jopt.simple export package joptsimple.util to module ...
            // （Forge 26.2 自带 jopt-simple 6.0-alpha-3，原版 26.2 声明 5.0.4，两份都在 classpath 里）
            // 所以按 group:artifact:classifier 去重，子版本覆盖父版本。
            var libraryIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            void AddLibrary(JsonElement lib)
            {
                var lp = GetLibraryPath(lib, mcFolder);
                if (lp == null)
                    return;

                var key = ArtifactKey(lib);
                if (key.Length > 0 && libraryIndex.TryGetValue(key, out var at))
                {
                    // jopt-simple 是唯一「父版本说了算」的库（见 SanitizeForgeClasspath）：
                    // 原版自带 5.0.4（自动模块名 jopt.simple），而 Forge 安装清单里额外带了
                    // 6.0-alpha-3（processor 工具，显式模块名 joptsimple）。按「子版本覆盖」
                    // 的默认规则会被换成后者，modlauncher 就再也找不到 jopt.simple 模块。
                    if (!IsJoptSimple(key))
                        libraries[at] = lp;     // 覆盖成子版本，位置不动
                    return;
                }

                if (key.Length > 0)
                    libraryIndex[key] = libraries.Count;
                libraries.Add(lp);
            }

            // 处理父版本
            if (versionJson.TryGetProperty("inheritsFrom", out var inh))
            {
                parentId = inh.GetString();
                diag($"Inherits from: {parentId}");
                var parentPath = Path.Combine(mcFolder, "versions", parentId, parentId + ".json");
                if (File.Exists(parentPath))
                {
                    var parentJson = JsonDocument.Parse(File.ReadAllText(parentPath)).RootElement;
                    if (parentJson.TryGetProperty("libraries", out var pl))
                        foreach (var lib in pl.EnumerateArray())
                            AddLibrary(lib);
                    if (parentJson.TryGetProperty("mainClass", out var pm))
                        mainClass = pm.GetString();
                    if (parentJson.TryGetProperty("arguments", out var pa))
                    {
                        if (pa.TryGetProperty("jvm", out var pj))
                            foreach (var a in pj.EnumerateArray())
                                if (a.ValueKind == JsonValueKind.String) jvmArgs.Add(a.GetString());
                        if (pa.TryGetProperty("game", out var pg))
                            foreach (var a in pg.EnumerateArray())
                                if (a.ValueKind == JsonValueKind.String) gameArgs.Add(a.GetString());
                    }
                    if (parentJson.TryGetProperty("assets", out var pas))
                        assetsIndex = pas.GetString();
                }
            }

            // 处理子版本（覆盖）
            if (versionJson.TryGetProperty("libraries", out var cl))
                foreach (var lib in cl.EnumerateArray())
                    AddLibrary(lib);
            if (versionJson.TryGetProperty("mainClass", out var cm))
                mainClass = cm.GetString();
            if (versionJson.TryGetProperty("arguments", out var ca))
            {
                // 子版本的参数追加到父版本后面（不替换）
                if (ca.TryGetProperty("jvm", out var cj))
                    foreach (var a in cj.EnumerateArray())
                        if (a.ValueKind == JsonValueKind.String) jvmArgs.Add(a.GetString());
                if (ca.TryGetProperty("game", out var cg))
                    foreach (var a in cg.EnumerateArray())
                        if (a.ValueKind == JsonValueKind.String) gameArgs.Add(a.GetString());
            }
            if (versionJson.TryGetProperty("assets", out var cas))
                assetsIndex = cas.GetString();

            // client.jar
            var clientJar = Path.Combine(mcFolder, "versions", versionId, versionId + ".jar");
            if (!File.Exists(clientJar) && parentId != null)
                clientJar = Path.Combine(mcFolder, "versions", parentId, parentId + ".jar");
            if (File.Exists(clientJar))
            {
                // 0 字节 / HTML 错误页也照「存在」算，但那会让游戏一启动就报 ClassNotFound ——
                // 官方源被限速或代理拦下时恰好会出现这种「文件在、内容不对」的情况。
                var size = new FileInfo(clientJar).Length;
                if (size < 512 * 1024 || !IsZipFile(clientJar))
                {
                    throw new InvalidDataException(
                        $"客户端文件损坏或未下载完整：{clientJar}（{size} 字节）。"
                        + "请在「下载中心」重新安装该版本，或把下载源切换为 BMCLAPI 镜像后重试。");
                }

                libraries.Add(clientJar);
            }
            else
            {
                diag($"WARNING: client.jar not found at {clientJar}");
                throw new FileNotFoundException(
                    $"缺少客户端文件：{clientJar}。"
                    + "该版本没有下载完整，请在「下载中心」重新安装（或切换下载源后重试）。");
            }

            // 检查缺失的库
            var missing = libraries.Where(l => !File.Exists(l)).ToList();
            if (missing.Count > 0)
            {
                // 提到 Warning 级：默认日志级别就能看到，排查「游戏起不来」时是关键线索
                Serilog.Log.Warning("[启动] {Version} 有 {Count} 个依赖库文件缺失：{Files}",
                    versionId, missing.Count, string.Join(", ", missing.Take(10)));
                diag($"WARNING: {missing.Count} missing libraries: {string.Join(", ", missing.Take(5))}");
            }
            libraries = libraries.Where(l => File.Exists(l)).ToList();

            // Forge 1.20.1 及更早分支的 classpath 清洗（见 SanitizeForgeClasspath）。
            // 必须在拼 classpath 之前做，否则这些 jar 会原样进 -cp。
            libraries = SanitizeForgeClasspath(libraries, jvmArgs, diag);

            var classpath = string.Join(";", libraries);
            diag($"Libraries: {libraries.Count}, MainClass: {mainClass}, Assets: {assetsIndex}");

            // 资源索引缺失时游戏会在加载资源阶段直接失败（1.7.3+ 必须有 assets/indexes/<id>.json）。
            // 官方源被限速 / 代理拦截时最容易只下到一半，这里提前给出可操作的提示，
            // 而不是让用户对着一个闪退的窗口猜。
            var needsAssetIndex = gameArgs.Any(a => a == "--assetIndex");
            var assetIndexPath = Path.Combine(mcFolder, "assets", "indexes", (assetsIndex ?? "latest") + ".json");
            if (needsAssetIndex && !File.Exists(assetIndexPath))
            {
                throw new FileNotFoundException(
                    $"缺少资源索引文件：{assetIndexPath}。"
                    + "说明该版本的资源没下载完整，请在「下载中心」重新安装该版本，"
                    + "或把下载源切换为 BMCLAPI 镜像后重试。");
            }
            diag($"JVM args: {string.Join(" ", jvmArgs)}");
            diag($"Game args count: {gameArgs.Count}");
            // 确认 fabric-loader 在 classpath 里
            var fabricLoaderInCp = libraries.Any(l => l.Contains("fabric-loader"));
            diag($"fabric-loader in classpath: {fabricLoaderInCp}");
            if (fabricLoaderInCp)
            {
                var fl = libraries.First(l => l.Contains("fabric-loader"));
                diag($"fabric-loader path: {fl}, exists: {File.Exists(fl)}, size: {(File.Exists(fl) ? new FileInfo(fl).Length : 0)}");
            }

            // 提取账户属性
            string accName = account?.Name ?? "Player";
            string accUuid = account?.Uuid.ToString() ?? "";
            string accToken = account?.AccessToken ?? "";
            bool isMicrosoft = forceMicrosoft || account is MicrosoftAccount;
            string accType = isMicrosoft ? "msa" : "legacy";
            diag($"Account: {accName}, type: {account?.GetType().Name}, isMicrosoft: {isMicrosoft}, userType: {accType}");

            var psi = new ProcessStartInfo
            {
                FileName = java.JavaPath,
                WorkingDirectory = mcFolder,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var nativesDir = Path.Combine(mcFolder, "versions", versionId, "natives");
            Directory.CreateDirectory(nativesDir);

            // 启动配置（设置页「启动」分类）：
            // 全部先夹紧再使用 —— 用户可能手改 settings.json 写出 0 / 负数，
            // -Xmx0m 之类会让 JVM 直接起不来，而报错信息完全看不出是配置问题。
            var cfg = SettingsService.Current;
            var maxMb = Math.Clamp(cfg.MaxMemoryMb, 512, 65536);
            var minMb = Math.Clamp(cfg.MinMemoryMb, 128, Math.Max(128, maxMb));

            // 版本隔离：开启时每个版本使用独立的游戏目录（saves / mods / config 等）
            var gameDir = cfg.VersionIsolation
                ? Path.Combine(mcFolder, "versions", versionId)
                : mcFolder;
            Directory.CreateDirectory(gameDir);
            diag($"Version isolation: {cfg.VersionIsolation}, gameDir: {gameDir}");

            psi.ArgumentList.Add($"-Xmx{maxMb}m");
            psi.ArgumentList.Add($"-Xms{minMb}m");
            diag($"Memory: -Xmx{maxMb}m -Xms{minMb}m, GC={cfg.GcMode}, IPv4Only={cfg.DisableIpv6}");

            // 垃圾回收器：不指定时交给 JVM 自己决定（新版 JVM 默认就是 G1）
            switch (cfg.GcMode)
            {
                case "G1GC":
                    psi.ArgumentList.Add("-XX:+UseG1GC");
                    break;
                case "ZGC":
                    psi.ArgumentList.Add("-XX:+UseZGC");
                    break;
                case "Parallel":
                    psi.ArgumentList.Add("-XX:+UseParallelGC");
                    break;
                case "Serial":
                    psi.ArgumentList.Add("-XX:+UseSerialGC");
                    break;
            }

            // 禁用 IPv6：部分网络环境下 Java 会优先走 AAAA 记录而连不上服务器
            if (cfg.DisableIpv6)
                psi.ArgumentList.Add("-Djava.net.preferIPv4Stack=true");

            // 离线账户皮肤：把本地皮肤文件路径通过 JVM 系统属性传给游戏，
            // 配合支持自定义皮肤的加载器（CustomSkinLoader / SkinPort 等）在游戏内生效。
            // 注意：皮肤文件按「带连字符的 UUID」命名（与账户页保存规则一致）。
            if (!isMicrosoft && !string.IsNullOrEmpty(accUuid))
            {
                var offlineSkinPath = Path.Combine(AppContext.BaseDirectory, "res", $"{accUuid}_full.png");
                if (File.Exists(offlineSkinPath))
                {
                    psi.ArgumentList.Add($"-Dminecraft.skin.path={offlineSkinPath}");
                    psi.ArgumentList.Add($"-Dvbl.skin.path={offlineSkinPath}");
                    diag($"Offline skin arg: {offlineSkinPath}");
                }
                else
                {
                    diag("Offline skin: no local skin file, skip skin args");
                }
            }
            // 添加 JVM 参数，替换占位符，跳过 -cp ${classpath}（我们自己加）
            //
            // ⚠️ 这里的占位符替换必须和官方启动器同一套。少替换一个，Forge 就起不来：
            //   1.20.1 的 jvm 参数里有
            //     -p ${library_directory}/cpw/mods/bootstraplauncher/...${classpath_separator}...
            //     -DignoreList=...,${version_name}.jar
            //   这些是 **launcher 负责替换** 的宏（版本 JSON 里永远保留字面量）。
            //   不替换 → --module-path 变成一串不存在的路径 → JVM 直接
            //   「Module bootstraplauncher not found」，游戏窗口都不出。
            var libraryDirectory = Path.Combine(mcFolder, "libraries");
            for (int i = 0; i < jvmArgs.Count; i++)
            {
                var arg = jvmArgs[i];
                if (arg == "-cp" && i + 1 < jvmArgs.Count && jvmArgs[i + 1].Contains("${classpath}"))
                {
                    i++; // 跳过 -cp 和 ${classpath}
                    continue;
                }
                arg = arg
                    .Replace("${natives_directory}", nativesDir)
                    .Replace("${library_directory}", libraryDirectory)
                    .Replace("${classpath_separator}", Path.PathSeparator.ToString())
                    .Replace("${version_name}", versionId)
                    .Replace("${game_directory}", gameDir)
                    .Replace("${assets_root}", Path.Combine(mcFolder, "assets"))
                    .Replace("${assets_index_name}", assetsIndex)
                    .Replace("${launcher_name}", "VibrantbitLauncher")
                    .Replace("${launcher_version}", "1.0")
                    .Replace("${classpath}", classpath);

                // ignoreList 的作用是「让 FML 别加载这个 jar」。
                // 版本 JSON 里写的是 ${version_name}.jar（实例自己的 jar），
                // 而版本隔离下的实际游戏 jar 常常是父版本那个（1.20.1.jar），
                // 名字对不上就等于没忽略 —— 补丁 jar 和原版 jar 会同时进模块层。
                if (arg.StartsWith("-DignoreList=", StringComparison.Ordinal))
                {
                    var clientJarName = Path.GetFileName(clientJar);
                    if (!arg.Contains(clientJarName, StringComparison.OrdinalIgnoreCase))
                        arg += "," + clientJarName;
                }

                psi.ArgumentList.Add(arg);
            }

            // 用户自定义 JVM 参数：一行一条，'#' 开头视为注释。
            // 放在 -cp 之前，这样用户可以用 -Dxxx=yyy 之类覆盖上面的默认值。
            foreach (var rawLine in (cfg.JvmArgs ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var custom = rawLine.Trim();
                if (custom.Length == 0 || custom.StartsWith("#"))
                    continue;

                psi.ArgumentList.Add(custom);
            }

            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(classpath);
            psi.ArgumentList.Add(mainClass);

            // 版本 JSON 里自带的窗体参数要先剔除：值是它的下一个元素，必须连值一起跳过，
            // 否则会留下一个游离的位置参数。剔除后统一由设置页的窗体大小决定。
            var cleanedGameArgs = new List<string>(gameArgs.Count);
            for (var i = 0; i < gameArgs.Count; i++)
            {
                var a = gameArgs[i];
                if (a is "--width" or "--height")
                {
                    i++;
                    continue;
                }
                if (a == "--fullscreen")
                    continue;

                cleanedGameArgs.Add(a);
            }

            var winW = Math.Clamp(cfg.WindowWidth, 320, 7680);
            var winH = Math.Clamp(cfg.WindowHeight, 240, 4320);

            foreach (var arg in cleanedGameArgs)
            {
                var replaced = arg
                    .Replace("${auth_player_name}", accName)
                    .Replace("${auth_uuid}", accUuid)
                    .Replace("${auth_access_token}", accToken)
                    .Replace("${version_name}", versionId)
                    .Replace("${game_directory}", gameDir)
                    .Replace("${assets_root}", Path.Combine(mcFolder, "assets"))
                    .Replace("${assets_index_name}", assetsIndex)
                    .Replace("${user_type}", accType)
                    .Replace("${version_type}", "release")
                    .Replace("${resolution_width}", winW.ToString())
                    .Replace("${resolution_height}", winH.ToString());
                psi.ArgumentList.Add(replaced);
            }

            // 窗体大小 / 全屏：1.13 以前的版本用的是老式 minecraftArguments，
            // 其 game 参数数组是空的（我们只补了空的 arguments.game），
            // 所以这两个参数必须由启动器显式传入。
            if (cfg.StartFullscreen)
            {
                psi.ArgumentList.Add("--fullscreen");
            }
            else
            {
                psi.ArgumentList.Add("--width");
                psi.ArgumentList.Add(winW.ToString());
                psi.ArgumentList.Add("--height");
                psi.ArgumentList.Add(winH.ToString());
            }
            diag($"Window: {(cfg.StartFullscreen ? "fullscreen" : $"{winW}x{winH}")}");

            // 版本隔离：显式添加 --gameDir 参数（确保游戏使用版本独立目录）
            if (!psi.ArgumentList.Contains("--gameDir"))
            {
                psi.ArgumentList.Add("--gameDir");
                psi.ArgumentList.Add(gameDir);
                diag($"Added --gameDir: {gameDir}");
            }

            // 授权与登录参数：根据账户类型添加
            var uuidNoHyphens = accUuid.Replace("-", "");
            void AddAuthArg(string name, string value)
            {
                if (!psi.ArgumentList.Contains(name))
                {
                    psi.ArgumentList.Add(name);
                    psi.ArgumentList.Add(value);
                }
            }
            AddAuthArg("--username", accName);
            AddAuthArg("--uuid", uuidNoHyphens);
            AddAuthArg("--userType", accType);
            if (isMicrosoft)
            {
                // 微软账户：添加认证参数
                AddAuthArg("--accessToken", accToken);
                diag($"Auth args: Microsoft account (username, uuid, accessToken, userType=msa)");
            }
            else
            {
                // 离线账户：只添加基础参数，不需要 accessToken/clientId/xuid
                // （皮肤已在上方通过 JVM 系统属性传入）
                diag($"Auth args: Offline account (username, uuid, userType=legacy)");
            }

            diag($"Total args: {psi.ArgumentList.Count}, first 3: {psi.FileName} {string.Join(" ", psi.ArgumentList.Take(3))}");

            var proc = new Process { StartInfo = psi };
            proc.Start();
            diag($"Process started: Id={proc.Id}");
            return proc;
        }

        /// <summary>
        /// 粗略判断一个文件是不是 zip/jar：只看前两个字节是不是 "PK"。
        /// 用来识别「下载到的是 HTML 错误页 / 空壳」——这类文件同样"存在"，但游戏一定起不来。
        /// </summary>
        private static bool IsZipFile(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                return fs.ReadByte() == 'P' && fs.ReadByte() == 'K';
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 库的「同一份工件」标识：group:artifact:classifier，**不含版本号**。
        /// 用来在合并父/子版本时判断「这是同一个库的另一个版本」，避免两个版本同时进 classpath。
        /// </summary>
        private static string ArtifactKey(JsonElement lib)
        {
            if (!lib.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                return string.Empty;

            var name = nameEl.GetString();
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            var at = name.IndexOf('@');
            if (at > 0)
                name = name[..at];

            var parts = name.Split(':');
            if (parts.Length < 2)
                return string.Empty;

            var classifier = parts.Length > 3 ? parts[3] : string.Empty;
            return string.Join(':', parts[0], parts[1], classifier);
        }

        /// <summary>
        /// 是否是 jopt-simple（modlauncher 的模块名 <c>jopt.simple</c> 就来自这个 jar）。
        /// </summary>
        private static bool IsJoptSimple(string key)
            => key.StartsWith("net.sf.jopt-simple:jopt-simple", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 处理器专用工具库的文件名前缀（小写）。这些 jar 只给安装期的 processor 用，
        /// **绝不该出现在启动 classpath 上**。
        /// </summary>
        private static readonly string[] ProcessorToolPrefixes =
        {
            "installertools-", "forgeautorenamingtool-", "jarsplitter-", "binarypatcher-",
            "srgutils-", "lzma-java-", "javaxdelta-", "fastcsv-", "trove-", "mcp_config-",
        };

        /// <summary>
        /// 「模块路径说了算」的库族：asm 系列。
        /// 它同时被 modlauncher（模块层）和普通类加载器使用，版本必须与 <c>-p</c> 里点名的一致，
        /// 多留一个旧版本就会撞包。
        /// </summary>
        private static readonly string[] ModuleAuthoritativePrefixes = { "asm-", "asm." };

        /// <summary>
        /// 清洗 Forge 系（有 <c>-p</c> 模块路径）的启动 classpath。
        ///
        /// <para>
        /// <b>为什么需要这一步</b>：1.0.11.0 之前的安装器把 <c>install_profile.libraries</c>
        /// （processor 的工具库：installertools / ForgeAutoRenamingTool / srgutils /
        /// jopt-simple 6.0-alpha-3 …）也写进了实例版本 JSON。这些 jar 一旦落到 <c>-cp</c> 上，
        /// 模块层就会当场炸掉，实测到的两种形态：
        /// </para>
        /// <list type="bullet">
        /// <item><c>Module jopt.simple not found, required by cpw.mods.modlauncher</c>
        /// —— 1.20.1 的 <c>-p</c> 里本来就没有 jopt，classpath 上那份 6.0-alpha-3 反而让
        /// modlauncher 的模块解析找到了名字却拿不到模块。</item>
        /// <item><c>Modules joptsimple and jopt.simple export package joptsimple.util</c>
        /// —— 5.0.4 与 6.0-alpha-3 同时在场（原版 1.20.1 自带 5.0.4）。</item>
        /// <item>工具库里的老 asm（9.2 / 9.6）还会按「子版本覆盖父版本」的规则
        /// 把官方声明的 9.9.1 顶掉，模块路径与 classpath 的 asm 版本就此分叉。</item>
        /// </list>
        ///
        /// <para>
        /// <b>判据</b>：<c>-p</c>（模块路径）是唯一权威 —— 它点名了哪些 jar，classpath 上
        /// 该库族就只留那一个；没被点名的处理器工具一律剔除。这样既修好老实例，
        /// 又不会动到 26.x（它们没有 <c>-p</c>，会原样返回）。
        /// </para>
        /// </summary>
        private static List<string> SanitizeForgeClasspath(List<string> libraries, List<string> jvmArgs, Action<string> diag)
        {
            // 收集模块路径里的文件名。这里只认文件名，不关心目录：
            // 参数值里还带着未替换的 ${library_directory} 占位符，取 Basename 即可。
            var moduleFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < jvmArgs.Count - 1; i++)
            {
                if (jvmArgs[i] != "-p")
                    continue;

                var raw = jvmArgs[i + 1];
                if (string.IsNullOrEmpty(raw))
                    continue;

                // 占位符（${classpath_separator} / ${library_directory} …）先换成 ';'，
                // 再按分隔符切开，最后每个片段取文件名 —— 目录部分本来也不需要。
                var normalized = System.Text.RegularExpressions.Regex.Replace(raw, @"\$\{[A-Za-z_]+\}", ";");
                foreach (var part in normalized.Split(new[] { ';', ':' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!part.Contains(".jar", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fileName = Path.GetFileName(part.Trim());
                    if (!string.IsNullOrEmpty(fileName))
                        moduleFiles.Add(fileName);
                }
            }

            // 没有模块路径 = 不是 Forge 1.20.1 那一支（26.x 走 classpath 引导），不要乱动
            if (moduleFiles.Count == 0)
                return libraries;

            var kept = new List<string>(libraries.Count);
            var dropped = new List<string>();
            var joptKept = false;
            foreach (var path in libraries)
            {
                var fileName = Path.GetFileName(path);
                var lower = fileName.ToLowerInvariant();

                // ① 处理器工具：直接剔除。它们只服务于安装期 processor，
                //    留在 classpath 上只会让模块层多出一堆不该存在的包。
                if (ProcessorToolPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
                {
                    dropped.Add(fileName);
                    continue;
                }

                // ② jopt-simple：**必须留一份**，但只能有一份。
                //    反直觉但实测如此 —— 1.20.1 的 -p 里并没有 jopt，modlauncher 的
                //    `requires jopt.simple` 是靠 classpath 上那份 jar 自动命名成模块来满足的
                //    （jopt-simple-5.0.4.jar → 自动模块名 jopt.simple）。
                //    全删掉就直接 `Module jopt.simple not found`；两份都留则
                //    `Modules joptsimple and jopt.simple export package joptsimple.util`
                //    （工具库那份 6.0-alpha-3 声明了显式模块名，和原版 5.0.4 撞包）。
                //    保留第一份 —— 父版本（原版）的排在前面，正是官方启动器用的那个。
                if (lower.StartsWith("jopt-simple-", StringComparison.Ordinal))
                {
                    if (joptKept)
                    {
                        dropped.Add(fileName);
                        continue;
                    }

                    joptKept = true;
                    kept.Add(path);
                    continue;
                }

                // ③ asm：以 -p 点名的版本为准，其余版本一律剔除
                if (ModuleAuthoritativePrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
                {
                    if (!moduleFiles.Contains(fileName))
                    {
                        dropped.Add(fileName);
                        continue;
                    }
                }

                kept.Add(path);
            }

            if (dropped.Count > 0)
            {
                Serilog.Log.Warning("[启动] 已从 classpath 剔除 {Count} 个处理器/重复库（会破坏模块层）：{Files}",
                    dropped.Count, string.Join(", ", dropped));
                diag($"Dropped {dropped.Count} processor/duplicate libs: {string.Join(", ", dropped)}");
            }

            return kept;
        }

        private static string GetLibraryPath(JsonElement lib, string mcFolder)
        {
            if (lib.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("artifact", out var art))
            {
                if (art.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                    return Path.Combine(mcFolder, "libraries", path.GetString());
            }
            if (lib.TryGetProperty("name", out var name))
            {
                var parts = name.GetString().Split(':');
                if (parts.Length >= 3)
                {
                    var group = parts[0].Replace('.', '/');
                    return Path.Combine(mcFolder, "libraries", group, parts[1], parts[2], $"{parts[1]}-{parts[2]}.jar");
                }
            }
            return null;
        }

        /// <summary>
        /// 按设置页「主界面 · 启动后行为」对启动器自身执行动作。
        ///
        /// 取值：None（不动）/ Minimize（最小化）/ Close（关闭启动器）。
        /// 关闭走 <c>Window.Close()</c> 而不是 <c>AppLifetime.Shutdown()</c>：
        /// 前者会正常触发主窗口的关闭流程（托盘 / 退出清理），后者是硬退出。
        /// </summary>
        private void ApplyAfterLaunchAction()
        {
            var action = SettingsService.Current.AfterLaunchAction;
            if (string.IsNullOrWhiteSpace(action) || action.Equals("None", StringComparison.OrdinalIgnoreCase))
                return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var window = AppLifetime.MainWindow;
                    if (window is null)
                        return;

                    if (action.Equals("Minimize", StringComparison.OrdinalIgnoreCase))
                    {
                        window.WindowState = Avalonia.Controls.WindowState.Minimized;
                        Serilog.Log.Debug("[启动] 启动后行为：已最小化启动器");
                    }
                    else if (action.Equals("Close", StringComparison.OrdinalIgnoreCase))
                    {
                        Serilog.Log.Debug("[启动] 启动后行为：关闭启动器");
                        window.Close();
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "[启动] 启动后行为执行失败");
                }
            });
        }

        private void ShowError(string msg) =>
            _notification.Error(msg);

        private void ShowInfo(string msg) => _notification.Show(msg, "提示");

        private void ShowSuccess(string msg) => _notification.Success(msg);

        /// <summary>
        /// 游戏输出的落地文件（启动器目录下的 log.txt）。
        ///
        /// <para>
        /// 只用一把锁 + 一个长期持有的写入器。以前是每写一行 <c>new StreamWriter("log.txt", true)</c>，
        /// 默认 <c>FileShare.Read</c> 意味着**第二个写者必然失败**：
        /// 同时开两个实例、或者上一个游戏进程还没退干净，就会抛
        /// <c>IOException: The process cannot access the file ... being used by another process</c>。
        /// </para>
        /// <para>
        /// 这个异常是从 <c>Process.OutputDataReceived</c> 的回调线程抛出来的，
        /// 谁都接不住 —— .NET 把它当「未处理的应用域异常」，**整个启动器直接消失**
        /// （日志里那行「未处理的应用域异常（进程即将终止）」就是这个）。
        /// 所以这里必须：① 用 FileShare.ReadWrite；② 任何异常都就地吞掉。
        /// </para>
        /// </summary>
        private static readonly object GameLogSync = new();
        private static StreamWriter? _gameLogWriter;
        private static DateTime _gameLogRetryAfter = DateTime.MinValue;

        void writeLog(string time, string message)
        {
            lock (GameLogSync)
            {
                try
                {
                    if (_gameLogWriter is null)
                    {
                        // 上一次打开失败（文件被别的程序独占）时隔一会儿再试，
                        // 否则每来一行输出都要撞一次异常
                        if (DateTime.UtcNow < _gameLogRetryAfter)
                            return;

                        var path = Path.Combine(AppContext.BaseDirectory, "log.txt");
                        var stream = new FileStream(
                            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        _gameLogWriter = new StreamWriter(stream) { AutoFlush = true };
                    }

                    _gameLogWriter.WriteLine($"{time}: {message}");
                }
                catch (Exception ex)
                {
                    _gameLogWriter?.Dispose();
                    _gameLogWriter = null;
                    _gameLogRetryAfter = DateTime.UtcNow.AddSeconds(30);
                    Serilog.Log.Debug(ex, "写 log.txt 失败（已忽略，游戏输出仍可在日志窗口查看）");
                }
            }
        }
    }
    /// <summary>启动页「最近游玩」里的一行。</summary>
    public class RecentInstance
    {
        public string Id { get; init; } = string.Empty;
        public string TimeText { get; init; } = string.Empty;
        public RelayCommand<string>? Command { get; init; }
    }

    public class LocalVersion
    {
        public string Version { get; set; }

        public RelayCommand<string> RunCommand { get; set; }
        public RelayCommand<string> SettingsCommand { get; set; }

        /// <summary>打开实例工具箱（依赖体检 / 配装分享）。</summary>
        public RelayCommand<string> ToolsCommand { get; set; }

    }
}
