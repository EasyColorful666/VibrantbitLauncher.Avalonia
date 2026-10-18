using VibrantbitLauncher.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MinecraftLaunch.Base.Interfaces;
using MinecraftLaunch.Base.Models.Game;
using MinecraftLaunch.Base.Models.Network;
using MinecraftLaunch.Components.Installer;
using MinecraftLaunch.Utilities;
using System;
using System.IO;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.Services.Download;
using VibrantbitLauncher.ViewModels.Windows;
using VibrantbitLauncher.Views.Windows;

namespace VibrantbitLauncher.ViewModels.Pages
{
    public class InstallPageViewModel : ObservableObject
    {

        /// <summary>
        /// 走自实现安装器的加载器种类。
        ///
        /// 这三种加载器原来都交给 MinecraftLaunch 的 <c>ForgeInstaller</c> / <c>QuiltInstaller</c>：
        /// 那条路径直连 maven.minecraftforge.net / maven.neoforged.net（国内基本连不上），
        /// 没有镜像改写、没有空闲超时，进度只报「0/1 + N/A」—— 用户看到的就是「卡在这一步」。
        /// </summary>
        private enum LoaderKind
        {
            Forge,
            NeoForge,
            Quilt,
        }

        private string mcVersion = string.Empty;
        private string mcFolder = "./.minecraft";
        private List<string> forgeVersions = new();
        private List<string> fabricVersions = new();
        private List<string> neoforgeVersions = new();
        private List<string> quiltVersions = new();
        private string selectForgeVersion = string.Empty;
        private string selectFabricVersion = string.Empty;
        private string selectNeoforgeVersion = string.Empty;
        private string selectQuiltVersion = string.Empty;
        private int installProgress = 0;
        private string installStep = string.Empty;
        private string speed;
        private readonly INotificationService _notification = AppServices.Notifications!;
        private bool isLoadingVersions = false;
        private bool isInstalling = false;

        public RelayCommand InstallCommand { get; set; }
        public RelayCommand RefreshCommand { get; set; }
        public RelayCommand LoadCommand { get; set; }

        public List<string> ForgeVersions
        {
            get => forgeVersions;
            set => SetProperty(ref forgeVersions, value);
        }
        public List<string> FabricVersions
        {
            get => fabricVersions;
            set => SetProperty(ref fabricVersions, value);
        }
        public List<string> NeoforgeVersions
        {
            get => neoforgeVersions;
            set => SetProperty(ref neoforgeVersions, value);
        }
        public List<string> QuiltVersions
        {
            get => quiltVersions;
            set => SetProperty(ref quiltVersions, value);
        }
        public string McVersion
        {
            get => mcVersion;
            set => SetProperty(ref mcVersion, value);
        }
        public string SelectForgeVersion
        {
            get => selectForgeVersion;
            set => SetProperty(ref selectForgeVersion, value);
        }
        public string SelectFabricVersion
        {
            get => selectFabricVersion;
            set => SetProperty(ref selectFabricVersion, value);
        }
        public string SelectNeoforgeVersion
        {
            get => selectNeoforgeVersion;
            set => SetProperty(ref selectNeoforgeVersion, value);
        }
        public string SelectQuiltVersion
        {
            get => selectQuiltVersion;
            set => SetProperty(ref selectQuiltVersion, value);
        }
        public int InstallProgress
        {
            get => installProgress;
            set => SetProperty(ref installProgress, value);
        }
        public string InstallStep
        {
            get => installStep;
            set => SetProperty(ref installStep, value);
        }
        public string Speed
        {
            get => speed;
            set => SetProperty(ref speed, value);
        }
        /// <summary>正在拉取四个加载器的版本列表。</summary>
        public bool IsLoadingVersions
        {
            get => isLoadingVersions;
            set
            {
                if (SetProperty(ref isLoadingVersions, value))
                    RaiseBusyStateChanged();
            }
        }

        /// <summary>正在执行安装。</summary>
        public bool IsInstalling
        {
            get => isInstalling;
            set
            {
                if (SetProperty(ref isInstalling, value))
                    RaiseBusyStateChanged();
            }
        }

        /// <summary>任一耗时操作进行中（拉列表 / 安装）。</summary>
        public bool IsLoading => IsLoadingVersions || IsInstalling;

        /// <summary>
        /// 「安装」按钮是否可用：只看有没有正在安装。
        ///
        /// 原先这里是 <c>!IsLoading</c>，而 IsLoading 在进入页面拉取加载器版本列表时也为 true，
        /// 于是刚进页面那几秒按钮会被判成禁用；若列表加载中途抛异常，
        /// 标志位还会一直停在 true，按钮就永久灰掉了（用户反馈的「误判禁用」）。
        /// 列表是否加载完与「能不能装原版」无关，所以这里把它从判断里摘掉。
        /// </summary>
        public bool CanInstall => !IsInstalling;

        /// <summary>「刷新」按钮是否可用：拉列表和安装期间都不允许。</summary>
        public bool CanRefresh => !IsLoadingVersions && !IsInstalling;

        /// <summary>忙碌状态（或由其派生的可用性）变化后，统一刷新绑定与命令状态。</summary>
        private void RaiseBusyStateChanged()
        {
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanRefresh));
            InstallCommand?.NotifyCanExecuteChanged();
            RefreshCommand?.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// 安全地取第一个可用的 Java 路径；枚举过程失败或没有候选时返回 null。
        /// </summary>
        private static async Task<string?> GetFirstJavaPathAsync()
        {
            try
            {
                return await JavaHelper.FindFirstJavaPathAsync();
            }
            catch
            {
                return null;
            }
        }

        private bool forgeAvailable = true;
        private bool fabricAvailable = true;
        private bool neoforgeAvailable = true;
        private bool quiltAvailable = true;

        public bool ForgeAvailable { get => forgeAvailable; set => SetProperty(ref forgeAvailable, value); }
        public bool FabricAvailable { get => fabricAvailable; set => SetProperty(ref fabricAvailable, value); }
        public bool NeoforgeAvailable { get => neoforgeAvailable; set => SetProperty(ref neoforgeAvailable, value); }
        public bool QuiltAvailable { get => quiltAvailable; set => SetProperty(ref quiltAvailable, value); }

        public InstallPageViewModel(string McVersion)
        {
            this.mcVersion = McVersion;
            this.mcFolder = Path.GetFullPath(SettingsService.Current.MinecraftFolder ?? "./.minecraft");
            InstallCommand = new RelayCommand(async () => await InstallAsync(), () => CanInstall);
            RefreshCommand = new RelayCommand(async () => await LoadVersionsAsync(), () => CanRefresh);
            LoadCommand = new RelayCommand(async () => await Load());
        }

        private string? versionsLoadedFor;

        public async Task Load()
        {

            // 进入安装页时 SetMcVersion 和页面 Loaded 都会触发加载。
            // 这里按版本号防重，避免同一次导航重复发起 4 个加载器的在线查询。
            if (string.Equals(versionsLoadedFor, mcVersion, StringComparison.OrdinalIgnoreCase))
                return;

            await LoadVersionsAsync();
            versionsLoadedFor = mcVersion;
        }

        public async Task LoadVersionsAsync()
        {
            IsLoadingVersions = true;
            try
            {
                ForgeAvailable = FabricAvailable = NeoforgeAvailable = QuiltAvailable = true;
                var errors = new ConcurrentBag<string>();

                // 四家加载器**并行**查询。原来是顺序 await，只要其中一家（都是境外站点）
                // 卡住，后面三家就得排队干等 —— 低配机 / 弱网下表现为「安装页一直转圈」。
                var forgeTask = LoadLoaderAsync(
                    async () => (await ForgeInstaller.EnumerableForgeAsync(mcVersion))
                        .Select(x => (ForgeInstallEntry)x)
                        .Where(x => !x.IsNeoforge)
                        .Select(x => x.DisplayVersion).ToList(),
                    () => ForgeAvailable = false, errors, "Forge");

                var fabricTask = LoadLoaderAsync(
                    // 走自实现版本：自带镜像兜底 / 精确超时 / 重试，
                    // 库自带的 EnumerableFabricAsync 是直连 meta.fabricmc.net 的裸请求。
                    async () => (await FabricDownloader.EnumerableFabricAsync(mcVersion))
                        .Select(x => x.DisplayVersion).ToList(),
                    () => FabricAvailable = false, errors, "Fabric");

                var neoforgeTask = LoadLoaderAsync(
                    async () => (await ForgeInstaller.EnumerableForgeAsync(mcVersion, true))
                        .Select(x => (ForgeInstallEntry)x)
                        .Where(x => x.IsNeoforge)
                        .Select(x => x.DisplayVersion).ToList(),
                    () => NeoforgeAvailable = false, errors, "NeoForge");

                var quiltTask = LoadLoaderAsync(
                    async () => (await QuiltInstaller.EnumerableQuiltAsync(mcVersion)).Select(x => x.DisplayVersion).ToList(),
                    () => QuiltAvailable = false, errors, "Quilt");

                await Task.WhenAll(forgeTask, fabricTask, neoforgeTask, quiltTask);

                ForgeVersions = forgeTask.Result;
                FabricVersions = fabricTask.Result;
                NeoforgeVersions = neoforgeTask.Result;
                QuiltVersions = quiltTask.Result;

                if (errors.Count > 0)
                {
                    _notification.Show($"部分加载器列表加载失败：{string.Join("、", errors)}", "提示", NotificationSeverity.Informational);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "加载版本列表失败");
            }
            finally
            {
                IsLoadingVersions = false;
            }
        }

        /// <summary>单家加载器查询的最长等待。境外站点在部分线路上会长时间无响应。</summary>
        private static readonly TimeSpan LoaderQueryTimeout = TimeSpan.FromSeconds(25);

        private async Task<List<string>> LoadLoaderAsync(
            Func<Task<List<string>>> loadFunc,
            Action disableAction,
            ConcurrentBag<string> errors,
            string loaderName)
        {
            try
            {
                var work = Task.Run(loadFunc);

                // 没有这层保护的话，「加载版本列表」可以一直转下去：
                // 库内部的重试 + 100 秒默认超时叠起来，最坏能拖好几分钟。
                if (await Task.WhenAny(work, Task.Delay(LoaderQueryTimeout)) != work)
                {
                    // 放弃等待，但要让这个任务自己的异常有人接手，否则会变成未观测异常
                    _ = work.ContinueWith(
                        t => Serilog.Log.Warning(t.Exception, "{Loader} 查询超时后才返回", loaderName),
                        TaskContinuationOptions.OnlyOnFaulted);

                    Serilog.Log.Warning("{Loader} 版本列表查询超过 {Sec}s，已放弃", loaderName, LoaderQueryTimeout.TotalSeconds);
                    errors.Add($"{loaderName}(查询超时)");
                    return new List<string>();
                }

                return await work;
            }
            catch (Exception ex) when (ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("deserialization", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("missing required", StringComparison.OrdinalIgnoreCase))
            {
                // 加载器不支持该版本（404 或 API 返回格式不兼容），静默置灰
                disableAction();
                return new List<string>();
            }
            catch (Exception ex)
            {
                errors.Add($"{loaderName}({ex.Message})");
                return new List<string>();
            }
        }

        /// <summary>
        /// 安装入口：只负责占用 / 释放「正在安装」标志。
        ///
        /// 原先整个流程只有开头一次 <c>IsLoading = true</c>、结尾一次 <c>IsLoading = false</c>，
        /// 而中间好几处网络调用（Java 探测、版本清单、加载器枚举）都在 try 之外，
        /// 一旦抛异常就会从 async void 命令里逃逸出去，标志位永远停在 true ——
        /// 表现正是「安装按钮被误判禁用」，且之后再也点不动。这里用 try/finally 兜死。
        /// </summary>
        public async Task InstallAsync()
        {
            if (IsInstalling)
                return;

            IsInstalling = true;
            try
            {
                await InstallCoreAsync();
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "安装流程异常");
                // 不再直接弹 ex.Message：超时抛的是 TaskCanceledException，原文是英文
                // “The operation was canceled.”，用户看不出是网络问题。
                _notification.Error(FriendlyError.Describe(ex), "安装失败");
            }
            finally
            {
                IsInstalling = false;
            }
        }

        private async Task InstallCoreAsync()
        {
            InstallProgress = 0;
            InstallStep = string.Empty;
            Speed = string.Empty;
            var javaList = await JavaHelper.FindJavasAsync();
            var asyncJavas = javaList.ToList();

            if (asyncJavas.Count == 0)
            {
                _notification.Error("未找到 Java，请先在设置中选择 Java", "错误");
                return;
            }

            var taskService = App.Services.GetService(typeof(DownloadTaskService)) as DownloadTaskService;

            // 所有模组加载器都需要原版先安装（FabricInstaller 内部会查找已安装原版）
            bool hasLoader = !string.IsNullOrEmpty(SelectForgeVersion)
                || !string.IsNullOrEmpty(SelectFabricVersion)
                || !string.IsNullOrEmpty(SelectQuiltVersion)
                || !string.IsNullOrEmpty(SelectNeoforgeVersion);

            // 兜底守卫：UI 现在是单选（勾一个就禁用并清掉其余三个），
            // 但下面按 Forge → Fabric → Quilt → NeoForge 的顺序只取**第一个**非空版本号，
            // 万一将来又多了一个入口没守住单选，就会「勾了两个却只装一个」且不报错。
            // 这里显式拦下来，让问题当场暴露而不是变成玩家的困惑。
            var loaderCount = new[]
            {
                SelectForgeVersion, SelectFabricVersion, SelectQuiltVersion, SelectNeoforgeVersion
            }.Count(v => !string.IsNullOrEmpty(v));

            if (loaderCount > 1)
            {
                _notification.Error("同一个实例只能安装一个模组加载器，请只勾选其中一个", "提示");
                return;
            }

            if (hasLoader)
            {
                var vanillaJsonPath = Path.Combine(mcFolder, "versions", mcVersion, mcVersion + ".json");
                if (!File.Exists(vanillaJsonPath))
                {
                    App.Current.Dispatcher.Invoke((Action)(() =>
                        _notification.Show($"正在安装原版 {mcVersion}...", "提示", NotificationSeverity.Informational)));
                    var vanillas = await VibrantbitLauncher.Services.MinecraftVersionCache.GetAsync();
                    var vanillaEntry = vanillas.FirstOrDefault(x => x.McVersion == mcVersion);
                    if (vanillaEntry == null)
                    {
                        _notification.Error($"未找到原版版本: {mcVersion}", "错误");
                        return;
                    }
                    var vanillaTask = taskService?.CreateTask($"安装原版 {mcVersion}", DownloadTaskType.GameInstall);
                    // 记下目标版本：任务中心点「查看详情」时靠它跳回本版本的安装进度页
                    if (vanillaTask is not null)
                        vanillaTask.TargetMcVersion = mcVersion;
                    await VanillaDownloader.InstallAsync(mcFolder, (VersionManifestEntry)vanillaEntry, (p, s, sp) =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(new Action(() =>
                        {
                            InstallStep = s;
                            InstallProgress = p;
                            Speed = sp;
                        }));
                        taskService?.UpdateProgress(vanillaTask, p, sp, s);
                    });
                    taskService?.CompleteTask(vanillaTask, $"原版 {mcVersion} 安装完成");
                }
                else
                {
                    // 原版已经有了，但可能是别家启动器留下的老 JSON（缺 arguments）。
                    // 下面要用库的解析器读它，先补一遍，否则会抛 InvalidOperationException。
                    VersionJsonNormalizer.NormalizeFile(vanillaJsonPath);
                }
            }

            // Forge / NeoForge 的安装器需要一个 Java 路径。
            // 原实现直接 .First()，没有可用 Java 时会抛 InvalidOperationException（枚举本身也可能抛）。
            string? installerJavaPath = null;
            if (!string.IsNullOrEmpty(SelectForgeVersion) || !string.IsNullOrEmpty(SelectNeoforgeVersion))
            {
                installerJavaPath = await GetFirstJavaPathAsync();
                if (installerJavaPath == null)
                {
                    _notification.Error("未检测到可用的 Java 运行环境，请先在设置里配置 Java 路径", "错误");
                    return;
                }
            }

            // 确定安装器和入口
            InstallerBase installer = null;
            // 非空表示走自实现的原版安装器（库的 VanillaInstaller 装不了 1.13 以前的版本）
            VersionManifestEntry plainVanilla = null;
            // Fabric 也走自实现安装器（见 FabricDownloader 注释）。用一个标记代替
            // 「造一个 FabricInstaller 实例当路标」的写法 —— 后者为了拿到 entry，
            // 必须再联网查一次加载器列表，那正是超时的高发点。
            bool isFabric = false;
            // 非空表示走自实现的加载器安装器（Forge / NeoForge / Quilt）。
            // 这些加载器原来交给 MinecraftLaunch 的 ForgeInstaller / QuiltInstaller，
            // 那条路径没有镜像改写也没有超时，是「卡住装不上」的主要来源。
            (LoaderKind Kind, string Version)? loaderInstall = null;
            IInstallEntry entry;
            string taskName;

            if (!string.IsNullOrEmpty(SelectForgeVersion))
            {
                // 不再走 MinecraftLaunch 的 ForgeInstaller：它直连 maven.minecraftforge.net，
                // 国内几乎必卡（没有镜像改写、没有空闲超时、进度只报「0/1 + N/A」，
                // 用户看到的就是「卡在这一步装不上」）。改走自实现的 LoaderInstaller，
                // 它有 BMCLAPI 镜像兜底、空闲超时、以及 1.0.7.15 的多线程下载内核。
                loaderInstall = (LoaderKind.Forge, SelectForgeVersion!);
                taskName = $"安装 Forge {SelectForgeVersion}";
            }
            else if (!string.IsNullOrEmpty(SelectFabricVersion))
            {
                // 走自实现的 FabricDownloader（镜像兜底 / 精确超时 / 重试 / 并行下载）。
                //
                // 这里**不再**联网查一次加载器列表：安装只需要版本号字符串，
                // 原来的 FabricInstaller.EnumerableFabricAsync + FabricInstaller.Create
                // 纯属重复请求，而且那次请求没有镜像、没有重试、超时还是 100 秒 ——
                // 用户看到的那条 HTTP 超时就是从这条路径上来的（见 FabricDownloader 注释）。
                isFabric = true;
                taskName = $"安装 Fabric {SelectFabricVersion}";
            }
            else if (!string.IsNullOrEmpty(SelectQuiltVersion))
            {
                // 同理：库的 QuiltInstaller 会踩 MinecraftLibrary.ParseJsonNode 的
                // Nullable.Value 崩溃（见 LoaderInstaller.InstallQuiltAsync 的注释）。
                loaderInstall = (LoaderKind.Quilt, SelectQuiltVersion!);
                taskName = $"安装 Quilt {SelectQuiltVersion}";
            }
            else if (!string.IsNullOrEmpty(SelectNeoforgeVersion))
            {
                // NeoForge 的制品在 maven.neoforged.net，国内基本连不上（实测直连失败），
                // 只能走 BMCLAPI 的 /maven/ 镜像 —— 库的 ForgeInstaller 不知道这件事。
                loaderInstall = (LoaderKind.NeoForge, SelectNeoforgeVersion!);
                taskName = $"安装 NeoForge {SelectNeoforgeVersion}";
            }
            else
            {
                var vanillas = await VibrantbitLauncher.Services.MinecraftVersionCache.GetAsync();
                entry = vanillas.FirstOrDefault(x => x.McVersion == mcVersion);
                if (entry == null) { _notification.Error($"未找到原版版本: {mcVersion}", "错误"); return; }
                // 原版交给 VanillaDownloader（自实现），理由见该类注释
                plainVanilla = (VersionManifestEntry)entry;
                taskName = $"安装原版 {mcVersion}";
            }

            // 设置父原版版本（只有加载器需要）
            if (installer is not null and not VanillaInstaller)
            {
                // 原版 JSON 可能是别家启动器留下的老格式，解析前补一遍 arguments
                VersionJsonNormalizer.NormalizeFile(
                    Path.Combine(mcFolder, "versions", mcVersion, mcVersion + ".json"));

                var parser = new MinecraftParser(mcFolder);
                var parent = parser.GetMinecraft(mcVersion);
                if (parent != null)
                {
                    installer.GetType().GetProperty("InheritedMinecraft")?.SetValue(installer, parent);
                }
            }

            var task = taskService?.CreateTask(taskName, DownloadTaskType.GameInstall);
            // 记下目标版本：任务中心点「查看详情」时靠它跳回本版本的安装进度页
            if (task is not null)
                task.TargetMcVersion = mcVersion;

            // 捕获安装器内部吞掉的真实异常
            Exception installException = null;
            var completedEvent = installer?.GetType().GetEvent("Completed");
            if (completedEvent != null)
            {
                var argsType = completedEvent.EventHandlerType.GetGenericArguments()[0];
                void Handler(object s, object e)
                {
                    var okProp = argsType.GetProperty("IsSuccessful") ?? argsType.GetProperty("IsSuccess");
                    bool ok = okProp != null && (bool)okProp.GetValue(e);
                    if (!ok)
                    {
                        var exProp = argsType.GetProperty("Exception") ?? argsType.GetProperty("Error");
                        if (exProp != null)
                            installException = exProp.GetValue(e) as Exception;
                    }
                }
                var act = new Action<object, object>(Handler);
                var del = Delegate.CreateDelegate(completedEvent.EventHandlerType, act.Target, act.Method);
                completedEvent.AddEventHandler(installer, del);
            }

            if (installer is not null)
            {
                installer.ProgressChanged += (_, arg) =>
                {
                    InstallStep = $"{arg.FinishedStepTaskCount}/{arg.TotalStepTaskCount} ";
                    InstallProgress = (int)(arg.Progress * 100);
                    Speed = (arg.IsStepSupportSpeed ? $"{arg.Speed / 1024 / 1024}" : "N/A");
                    taskService?.UpdateProgress(task, InstallProgress,
                        arg.IsStepSupportSpeed ? $"{arg.Speed / 1024 / 1024:F1} MB/s" : string.Empty,
                        InstallStep);
                };
            }

            App.Current.Dispatcher.Invoke((Action)(() =>
                _notification.Show(taskName, "提示", NotificationSeverity.Informational)));

            try
            {
                MinecraftEntry minecraft;

                if (plainVanilla != null)
                {
                    // 原版：走自实现安装器（库的 VanillaInstaller 装不了 1.13 以前的版本）
                    minecraft = await VanillaDownloader.InstallAsync(mcFolder, plainVanilla, (p, s, sp) =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(new Action(() =>
                        {
                            InstallProgress = p;
                            InstallStep = s;
                            Speed = sp;
                        }));
                        taskService?.UpdateProgress(task, p, sp, s);
                    });
                }
                else if (isFabric)
                {
                    // 使用自实现的 FabricDownloader，绕过库的 bug
                    var customId = $"{mcVersion}_fabric_{SelectFabricVersion}";
                    minecraft = await FabricDownloader.InstallAsync(
                        mcFolder, mcVersion, SelectFabricVersion, customId,
                        (p, s) =>
                        {
                            // 库文件是并行下载的，这个回调会从工作线程进来，
                            // 必须显式切回 UI 线程再改绑定属性（原实现是串行下载，
                            // 靠「await 之后回到 UI 上下文」侥幸成立，并行后就不成立了）。
                            //
                            // 速度统一取全局速度表：这一阶段同时在跑多个库，任何一个的
                            // 局部速度都不能代表整体，只有总速度才是用户看到的「下载速度」。
                            var speedText = DownloadSpeedometer.FormatSpeed();
                            Avalonia.Threading.Dispatcher.UIThread.Post(new Action(() =>
                            {
                                InstallProgress = p;
                                InstallStep = s;
                                Speed = speedText;
                            }));
                            taskService?.UpdateProgress(task, p, speedText, s);
                        });
                }
                else if (loaderInstall is { } planned)
                {
                    // 自实现的加载器安装：安装器 / 依赖库 / Forge 制品全部带 BMCLAPI 镜像兜底，
                    // 下载走 1.0.7.15 的多线程内核（分块并行 + 全局速度反馈）。
                    var instanceId = planned.Kind switch
                    {
                        LoaderKind.NeoForge => $"{mcVersion}_neoforge_{planned.Version}",
                        LoaderKind.Quilt => $"{mcVersion}_quilt_{planned.Version}",
                        _ => $"{mcVersion}_forge_{planned.Version}",
                    };

                    void Report(int percent, string text)
                    {
                        // 这一阶段同时在并行下多个库，速度只能取全局速度表 ——
                        // 任何一个文件的局部速度都不能代表进度条的整体感受。
                        var speedText = DownloadSpeedometer.FormatSpeed();
                        Avalonia.Threading.Dispatcher.UIThread.Post(new Action(() =>
                        {
                            InstallProgress = percent;
                            InstallStep = text;
                            Speed = speedText;
                        }));
                        taskService?.UpdateProgress(task, percent, speedText, text);
                    }

                    // Forge / NeoForge 的 processor 要读写原版 client.jar（占位符 {MINECRAFT_JAR}）。
                    // 只有版本 JSON、没有 jar 的实例（别家启动器留下的、上次装到一半中断的）
                    // 在这里先补齐 —— 否则会崩在 processor 阶段，用户只看到一段 Java 堆栈输出。
                    var clientJar = Path.Combine(mcFolder, "versions", mcVersion, mcVersion + ".jar");
                    if (planned.Kind != LoaderKind.Quilt && !File.Exists(clientJar))
                    {
                        Report(2, $"补全原版 {mcVersion} 文件");
                        var manifest = await MinecraftVersionCache.GetAsync();
                        var vanilla = manifest.FirstOrDefault(x => x.McVersion == mcVersion);
                        if (vanilla is not null)
                        {
                            await VanillaDownloader.InstallAsync(
                                mcFolder, (VersionManifestEntry)vanilla,
                                (p2, step, sp) => Report(p2, step));
                        }
                    }

                    if (planned.Kind == LoaderKind.Quilt)
                    {
                        await LoaderInstaller.InstallQuiltAsync(
                            mcFolder, mcVersion, planned.Version, instanceId, Report);
                    }
                    else
                    {
                        await LoaderInstaller.InstallForgeLikeAsync(
                            mcFolder, mcVersion, planned.Version, instanceId,
                            planned.Kind == LoaderKind.NeoForge, installerJavaPath!, Report);
                    }

                    minecraft = new MinecraftParser(mcFolder).GetMinecraft(instanceId)
                        ?? throw new InvalidOperationException(
                            $"{(planned.Kind == LoaderKind.Quilt ? "Quilt" : "Forge")} 安装完成但解析版本失败：{instanceId}");
                }
                else
                {
                    minecraft = await installer.InstallAsync();
                }

                taskService?.CompleteTask(task, $"安装完成: {minecraft.Id}");
                App.Current.Dispatcher.Invoke((Action)(() =>
                {
                    _notification.Success($"安装完成: {minecraft.Id}", "安装完成");
                }));
            }
            catch (Exception ex)
            {
                var realMsg = installException != null ? DescribeError(installException) : DescribeError(ex);
                Serilog.Log.Error(installException ?? ex, "安装失败：{Message}", realMsg);
                taskService?.FailTask(task, realMsg);
                App.Current.Dispatcher.Invoke((Action)(() =>
                {
                    _notification.Error(realMsg, "安装失败");
                }));
            }
        }

        /// <summary>
        /// 把异常翻成人看得懂、且知道下一步该做什么的话。
        ///
        /// 之前用户看到的原文是
        /// <c>The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.</c>
        /// —— 既没有中文，也没告诉他可以开镜像。
        /// </summary>
        private static string DescribeError(Exception ex)
        {
            if (ex is AggregateException agg && agg.InnerException != null)
                ex = agg.InnerException;

            // 自实现下载器已经把原因和做法写好了，直接用
            if (ex is DownloadFailedException)
                return ex.Message;

            if (ex is OperationCanceledException || ex is TimeoutException
                || ex.InnerException is TimeoutException
                || ex.Message.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("Timeout", StringComparison.Ordinal))
            {
                return "网络连接超时：与 Minecraft / 加载器服务器通信失败。\n" +
                       "可在「设置 → 下载」把下载源切换为 BMCLAPI 镜像，或检查网络与代理后重试。";
            }

            if (ex is HttpRequestException)
            {
                return $"网络错误：{ex.Message}\n请检查网络连接或代理设置后重试。";
            }

            return ex.Message;
        }
    }
}
