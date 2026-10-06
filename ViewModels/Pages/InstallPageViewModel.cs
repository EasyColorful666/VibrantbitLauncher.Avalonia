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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Windows;
using VibrantbitLauncher.Views.Windows;

namespace VibrantbitLauncher.ViewModels.Pages
{
    public class InstallPageViewModel : ObservableObject
    {
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
                var errors = new List<string>();

                ForgeVersions = await LoadLoaderAsync(
                    async () => (await ForgeInstaller.EnumerableForgeAsync(mcVersion))
                        .Where(x => !((dynamic)x).IsNeoforge)
                        .Select(x => x.DisplayVersion).ToList(),
                    () => ForgeAvailable = false, errors, "Forge");

                FabricVersions = await LoadLoaderAsync(
                    async () => (await FabricInstaller.EnumerableFabricAsync(mcVersion)).Select(x => x.DisplayVersion).ToList(),
                    () => FabricAvailable = false, errors, "Fabric");

                NeoforgeVersions = await LoadLoaderAsync(
                    async () => (await ForgeInstaller.EnumerableForgeAsync(mcVersion))
                        .Where(x => ((dynamic)x).IsNeoforge)
                        .Select(x => x.DisplayVersion).ToList(),
                    () => NeoforgeAvailable = false, errors, "NeoForge");

                QuiltVersions = await LoadLoaderAsync(
                    async () => (await QuiltInstaller.EnumerableQuiltAsync(mcVersion)).Select(x => x.DisplayVersion).ToList(),
                    () => QuiltAvailable = false, errors, "Quilt");

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

        private async Task<List<string>> LoadLoaderAsync(
            Func<Task<List<string>>> loadFunc,
            Action disableAction,
            List<string> errors,
            string loaderName)
        {
            try
            {
                return await Task.Run(loadFunc);
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
                _notification.Error(ex.Message, "安装失败");
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
            IInstallEntry entry;
            string taskName;

            if (!string.IsNullOrEmpty(SelectForgeVersion))
            {
                var forges = await ForgeInstaller.EnumerableForgeAsync(mcVersion);
                entry = forges.FirstOrDefault(x => string.Equals(x.DisplayVersion, SelectForgeVersion, StringComparison.OrdinalIgnoreCase));
                if (entry == null) { _notification.Error($"未找到 Forge 版本: {SelectForgeVersion}", "错误"); return; }
                installer = ForgeInstaller.Create(mcFolder, installerJavaPath!, (ForgeInstallEntry)entry, $"{mcVersion}_forge_{SelectForgeVersion}");
                taskName = $"安装 Forge {SelectForgeVersion}";
            }
            else if (!string.IsNullOrEmpty(SelectFabricVersion))
            {
                var fabrics = await FabricInstaller.EnumerableFabricAsync(mcVersion);
                var fabricEntry = fabrics.FirstOrDefault(x => string.Equals(x.DisplayVersion, SelectFabricVersion, StringComparison.OrdinalIgnoreCase));
                if (fabricEntry == null)
                {
                    _notification.Error($"未找到 Fabric 版本: {SelectFabricVersion}", "错误");
                    return;
                }
                // McVersion 是计算属性，返回 Intermediary.Version；直接修正 Intermediary
                fabricEntry.Intermediary.Version = mcVersion;
                fabricEntry.Intermediary.Maven = $"net.fabricmc:intermediary:{mcVersion}";
                installer = FabricInstaller.Create(mcFolder, fabricEntry, $"{mcVersion}_fabric_{SelectFabricVersion}");
                taskName = $"安装 Fabric {fabricEntry.DisplayVersion}";
            }
            else if (!string.IsNullOrEmpty(SelectQuiltVersion))
            {
                var quilts = await QuiltInstaller.EnumerableQuiltAsync(mcVersion);
                entry = quilts.FirstOrDefault(x => string.Equals(x.DisplayVersion, SelectQuiltVersion, StringComparison.OrdinalIgnoreCase));
                if (entry == null) { _notification.Error($"未找到 Quilt 版本: {SelectQuiltVersion}", "错误"); return; }
                installer = QuiltInstaller.Create(mcFolder, (QuiltInstallEntry)entry, $"{mcVersion}_quilt_{SelectQuiltVersion}");
                taskName = $"安装 Quilt {SelectQuiltVersion}";
            }
            else if (!string.IsNullOrEmpty(SelectNeoforgeVersion))
            {
                var neoforges = (await ForgeInstaller.EnumerableForgeAsync(mcVersion,true)).ToList();
                entry = neoforges.FirstOrDefault(x => string.Equals(x.DisplayVersion, SelectNeoforgeVersion, StringComparison.OrdinalIgnoreCase));
                if (entry == null) { _notification.Error($"未找到 NeoForge 版本: {SelectNeoforgeVersion}", "错误"); return; }
                installer = ForgeInstaller.Create(mcFolder, installerJavaPath!, (ForgeInstallEntry)entry, $"{mcVersion}_neoforge_{SelectNeoforgeVersion}");
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
                else if (installer is FabricInstaller)
                {
                    // 使用自实现的 FabricDownloader，绕过库的 bug
                    var customId = $"{mcVersion}_fabric_{SelectFabricVersion}";
                    minecraft = await FabricDownloader.InstallAsync(
                        mcFolder, mcVersion, SelectFabricVersion, customId,
                        (p, s) =>
                        {
                            InstallProgress = p;
                            InstallStep = s;
                            taskService?.UpdateProgress(task, p, "", s);
                        });
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
                var realMsg = installException?.Message ?? ex.Message;
                Serilog.Log.Error(installException ?? ex, "安装失败：{Message}", realMsg);
                taskService?.FailTask(task, realMsg);
                App.Current.Dispatcher.Invoke((Action)(() =>
                {
                    _notification.Error(realMsg, "安装失败");
                }));
            }
        }
    }
}
