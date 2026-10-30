using CommunityToolkit.Mvvm.ComponentModel;
using VibrantbitLauncher.Helpers;
using CommunityToolkit.Mvvm.Input;
using MinecraftLaunch.Base.Models.Authentication;
using MinecraftLaunch.Base.Models.Game;
using MinecraftLaunch.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Serilog.Events;
using VibrantbitLauncher.Localization;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Windows;



namespace VibrantbitLauncher.ViewModels.Pages
{
    public partial class SettingsPageViewModel : ObservableObject, INavigationAware
    {
        private bool _isInitialized = false;
        private string _appVersion = string.Empty;
        private AppThemeMode _currentTheme = AppThemeMode.Light;

        // 游戏设置
        private string _minecraftFolder = "./.minecraft";
        private JavaEntry? _selectedJava;
        private ObservableCollection<JavaEntry> _javaEntries = new();

        // ===== 启动：Java 虚拟机与内存 =====
        private MemoryOption _selectedMaxMemory = MemoryOptions[2];   // 2 GB
        private MemoryOption _selectedMinMemory = MemoryOptions[1];   // 1 GB
        private string _jvmArgs = string.Empty;

        // ===== 启动：游戏目录 =====
        private bool _versionIsolation = true;

        // ===== 启动：高级 =====
        private bool _disableIpv6;
        private SettingOption _selectedGcMode = GcModeOptions[0];
        private int _windowWidth = 854;
        private int _windowHeight = 480;
        private bool _startFullscreen;

        // ===== 个性化：主界面 =====
        private SettingOption _selectedAfterLaunchAction = AfterLaunchOptions[0];

        // ===== 网络：下载 =====
        private bool _useBmclMirror;

        /// <summary>同时下载几个文件（文件级并发）。</summary>
        private int _downloadConcurrency = 6;

        /// <summary>单个文件切成几块并行（连接级并发，1 = 不分块）。</summary>
        private int _downloadChunkCount = 4;

        private int _downloadRetryCount = 4;

        /// <summary>
        /// 回填控件状态期间禁止把值写回配置。
        /// 启动 / 网络这两组新设置都走「UI 改 → 立即落盘」的路子，
        /// 初始化时逐个赋值会触发一串无意义的保存（还可能把还没读完的配置覆盖掉）。
        /// </summary>
        private bool _suppressSettingApply;

        public ICommand ChangeThemeCommand { get; private set; }
        public ICommand RefreshJavaCommand { get; private set; }

        /// <summary>打开当前的 .minecraft 目录（概览面板的快捷入口）。</summary>
        public RelayCommand OpenGameFolderCommand { get; }

        /// <summary>按本机内存自动挑一档最大内存。</summary>
        public RelayCommand AutoMemoryCommand { get; }

        // ===== 通用 / 关于 / 高级 的命令 =====
        public RelayCommand OpenAccountPageCommand { get; }
        public RelayCommand SignOutCommand { get; }
        public RelayCommand OpenVersionManageCommand { get; }
        public AsyncRelayCommand CheckUpdateCommand { get; }
        public AsyncRelayCommand ResetSettingsCommand { get; }
        public RelayCommand ApplyCustomAccentCommand { get; }
        public RelayCommand OpenWebsiteCommand { get; }
        public RelayCommand OpenRepositoryCommand { get; }

        /// <summary>在主窗口内嵌打开 AGPL-3.0 协议全文（正文已打进程序集，可离线阅读）。</summary>
        public RelayCommand OpenLicenseCommand { get; }

        public RelayCommand OpenChangelogCommand { get; }
        public RelayCommand OpenDocsCommand { get; }
        public RelayCommand OpenFeedbackCommand { get; }
        public RelayCommand OpenSponsorCommand { get; }
        public RelayCommand ClearResourcePacksCommand { get; }
        public RelayCommand ClearSavesCommand { get; }

        public SettingsPageViewModel()
        {
            ChangeThemeCommand = new RelayCommand<string>(OnChangeTheme);
            RefreshJavaCommand = new RelayCommand(async () => await LoadJavaAsync());
            OpenGameFolderCommand = new RelayCommand(OpenGameFolder);
            PickWallpaperCommand = new RelayCommand<WallpaperOption>(PickWallpaper);
            ClearBackgroundCommand = new RelayCommand(ClearBackground);
            ResetAppearanceCommand = new RelayCommand(ResetAppearance);
            SelectAccentCommand = new RelayCommand<AccentPreset>(SelectAccent);
            SelectGradientEndCommand = new RelayCommand<AccentPreset>(SelectGradientEnd);
            AutoMemoryCommand = new RelayCommand(AutoConfigureMemory);

            // ===== 通用 / 关于 / 高级 =====
            OpenAccountPageCommand = new RelayCommand(() => AppNavigation.Navigate(typeof(Views.Pages.AccountPage)));
            SignOutCommand = new RelayCommand(SignOut);
            OpenVersionManageCommand = new RelayCommand(() => AppNavigation.Navigate(typeof(Views.Pages.VersionManagePage)));
            CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync);
            ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync);
            ApplyCustomAccentCommand = new RelayCommand(ApplyCustomAccent);
            OpenWebsiteCommand = new RelayCommand(() => OpenUrl("https://vbl.easycolorful.cn"));
            // 「查看仓库」与「获取源代码」都指向仓库根：AGPL 要求提供源码获取途径，落到 releases 页算擦边。
            OpenRepositoryCommand = new RelayCommand(() => OpenUrl(UpdateService.RepositoryUrl));
            OpenLicenseCommand = new RelayCommand(ShowLicenseWindow);
            OpenChangelogCommand = new RelayCommand(() => OpenUrl($"{UpdateService.ReleasesPageUrl}"));
            OpenDocsCommand = new RelayCommand(() => OpenUrl("https://github.com/EasyColorful666/VibrantbitLauncher.Avalonia#readme"));
            OpenFeedbackCommand = new RelayCommand(() => OpenUrl("https://github.com/EasyColorful666/VibrantbitLauncher.Avalonia/issues"));
            OpenSponsorCommand = new RelayCommand(() => OpenUrl("https://vbl.easycolorful.cn"));
            ClearResourcePacksCommand = new RelayCommand(() => ResourcePacksFolder = string.Empty);
            ClearSavesCommand = new RelayCommand(() => SavesFolder = string.Empty);

            RefreshLogsCommand = new RelayCommand(RefreshLogs);
            ClearLogsCommand = new RelayCommand(ClearLogs);
            OpenLogFolderCommand = new RelayCommand(LogService.OpenLogDirectory);
            RefreshDiskUsageCommand = new AsyncRelayCommand(RefreshDiskUsageAsync);
            CleanExpiredLogsCommand = new RelayCommand(CleanExpiredLogs);

            // 订阅内存 sink：日志产生时实时出现在「最近日志」里。
            // 事件可能来自任意线程（下载、启动都是后台线程），必须切回 UI 线程再动集合。
            LogService.EntryWritten += OnLogEntryWritten;
        }

        private void OnLogEntryWritten(LogEntry entry)
        {
            var dispatcher = Dispatcher.UIThread;
            if (dispatcher == null)
                return;

            if (dispatcher.CheckAccess())
                AppendLog(entry);
            else
                dispatcher.Post(() => AppendLog(entry));
        }

        private void AppendLog(LogEntry entry)
        {
            if (!PassesFilter(entry))
                return;

            _recentLogs.Add(entry);

            // 与内存 sink 的容量保持一致，避免界面侧的集合无限增长
            while (_recentLogs.Count > MaxVisibleLogs)
                _recentLogs.RemoveAt(0);

            HasLogs = _recentLogs.Count > 0;
            UpdateLogSummary();
        }

        private bool PassesFilter(LogEntry entry) =>
            entry.Level >= _selectedLogFilterMinLevel;

        /// <summary>从内存缓冲重新灌一次列表（切过滤条件、进页面时用）。</summary>
        private void RefreshLogs()
        {
            _recentLogs.Clear();

            foreach (var entry in LogService.Snapshot())
            {
                if (!PassesFilter(entry))
                    continue;

                _recentLogs.Add(entry);
            }

            while (_recentLogs.Count > MaxVisibleLogs)
                _recentLogs.RemoveAt(0);

            HasLogs = _recentLogs.Count > 0;
            UpdateLogSummary(force: true);
        }

        private void ClearLogs()
        {
            var deleted = LogService.ClearAll();
            RefreshLogs();
            UpdateLogSummary(force: true);
            LogActionMessage = deleted > 0
                ? $"已删除 {deleted} 个日志文件，并从此刻重新记录"
                : "没有找到日志文件，已从此刻重新记录";
        }

        /// <summary>导出最新一个日志文件（由设置页的「导出日志」按钮调用）。</summary>
        public void ExportLog(string destination)
        {
            LogActionMessage = LogService.ExportLatest(destination)
                ? $"已导出到 {destination}"
                : "导出失败：没有可导出的日志文件，或目标与源文件相同";
        }

        /// <summary>上一次真正重算日志摘要的时间（用于限频）。</summary>
        private DateTime _lastLogSummaryUtc = DateTime.MinValue;

        /// <summary>
        /// 刷新日志摘要。
        ///
        /// 摘要要枚举日志目录并逐个取文件大小，而 Verbose / Debug 级别下每秒可能有几十条日志；
        /// 来一条算一次会把 UI 线程拖住（"日志一多界面就发涩"就是这么来的）。
        /// 所以默认限频，只有清空 / 切换页面这类用户显式操作才传 force 立即刷新。
        /// </summary>
        private void UpdateLogSummary(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && now - _lastLogSummaryUtc < TimeSpan.FromSeconds(2))
                return;

            _lastLogSummaryUtc = now;

            var files = LogService.GetLogFiles();
            var size = FormatSize(LogService.GetTotalSize());
            LogSummary = $"{files.Count} 个文件 · 共 {size} · 界面显示 {_recentLogs.Count} 条（最多 {MaxVisibleLogs} 条）";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        /// <summary>
        /// 导航进入设置页时调用（由 SettingsPage 控件转发过来）。
        ///
        /// 每次进入都重新检测 Java：用户可能刚装了新 JDK、或在别处改了 Java 路径，
        /// 切回设置页就应当看到最新的候选列表。首次进入还要做一次整体初始化。
        /// </summary>
        public async Task OnNavigatedToAsync()
        {
            if (!_isInitialized)
            {
                InitializeViewModel();
                return; // InitializeViewModel 内部已经触发过一次 Java 检测
            }

            // 每次重新进入：先让「主页」概览与当前配置对齐，再刷新日志 + 重新检测 Java。
            //
            // 只做日志和 Java 是不够的。本页是 DI 单例，InitializeViewModel 只跑一次，
            // 而配置会在别的地方被改：
            //   · 启动页「快捷设置」可以直接换游戏目录 / Java / 明暗主题（LaunchPage.OnQuickMcFolderClick）；
            //   · 下载中心可以换下载源；
            //   · 用户还可能手工编辑 settings.json，或者换了账号 / 实例后目录被重置。
            // 以前这些改动只有重启程序才会出现在这里，用户看到的就是
            // 「设置主页的配置不重启就不跟着变」。
            SyncOverviewFromSettings();
            FillExtendedSettings();
            RefreshLogs();
            await LoadJavaAsync();
        }

        /// <summary>
        /// 把「主页」概览里的只读字段与当前配置对齐。
        ///
        /// <para>
        /// 只写字段 + <see cref="ObservableObject.OnPropertyChanged(string?)"/>，<b>不走 setter</b>：
        /// setter 会把值回写进配置并立即落盘，而这里只是「把配置里的现状显示出来」，
        /// 不该产生任何写入，也不该触发 ApplyTheme / ApplyDownloadSettings 这类副作用。
        /// </para>
        /// </summary>
        private void SyncOverviewFromSettings()
        {
            // 游戏目录：启动页换目录、不同玩家用不同 .minecraft，都会落到这里
            var folder = SettingsService.ResolveMinecraftFolder();
            if (!string.Equals(_minecraftFolder, folder, StringComparison.Ordinal))
            {
                _minecraftFolder = folder;
                OnPropertyChanged(nameof(MinecraftFolder));
            }

            // 内存档位：概览里的 -Xmx / -Xms 由这两个字段算出来
            var maxMemory = MemoryOptions.FirstOrDefault(o => o.Mb == SettingsService.Current.MaxMemoryMb)
                ?? MemoryOptions[2];
            if (!ReferenceEquals(maxMemory, _selectedMaxMemory))
            {
                _selectedMaxMemory = maxMemory;
                OnPropertyChanged(nameof(SelectedMaxMemory));
            }

            var minMemory = MemoryOptions.FirstOrDefault(o => o.Mb == SettingsService.Current.MinMemoryMb)
                ?? MemoryOptions[1];
            if (!ReferenceEquals(minMemory, _selectedMinMemory))
            {
                _selectedMinMemory = minMemory;
                OnPropertyChanged(nameof(SelectedMinMemory));
            }

            OnPropertyChanged(nameof(MemoryHint));

            // Java 路径取的是配置值（见 JavaPathText）：启动页快捷设置能直接换 Java
            OnPropertyChanged(nameof(JavaPathText));

            // 下载源摘要 = 源 + 文件并发 + 单文件分块数，三项任一变了都要重算
            var mirror = SettingsService.Current.UseBmclMirror;
            var concurrency = DownloadConcurrencyOptions.Contains(SettingsService.Current.DownloadConcurrency)
                ? SettingsService.Current.DownloadConcurrency
                : 6;
            var chunks = DownloadChunkOptions.Contains(SettingsService.Current.DownloadChunkCount)
                ? SettingsService.Current.DownloadChunkCount
                : 4;

            if (mirror != _useBmclMirror
                || concurrency != _downloadConcurrency
                || chunks != _downloadChunkCount)
            {
                _useBmclMirror = mirror;
                _downloadConcurrency = concurrency;
                _downloadChunkCount = chunks;

                OnPropertyChanged(nameof(UseBmclMirror));
                OnPropertyChanged(nameof(DownloadSourceText));
                OnPropertyChanged(nameof(DownloadSourceSummary));
                OnPropertyChanged(nameof(DownloadConcurrency));
                OnPropertyChanged(nameof(DownloadChunkCount));
            }

            // 主题：启动页快捷设置里可以直接切明暗
            var theme = SettingsService.Current.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
                ? AppThemeMode.Dark
                : AppThemeMode.Light;
            if (theme != _currentTheme)
            {
                _currentTheme = theme;
                OnPropertyChanged(nameof(CurrentTheme));
                OnPropertyChanged(nameof(CurrentThemeText));
            }

            OnPropertyChanged(nameof(MachineMemoryText));
        }

        Task INavigationAware.OnNavigatedFromAsync() => Task.CompletedTask;

        private void InitializeViewModel()
        {
            // 回填控件状态期间不要触发「应用外观」，否则会把还没读完的配置写回去
            _suppressAppearanceApply = true;
            try
            {
                CurrentTheme = SettingsService.Current.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
                    ? AppThemeMode.Dark
                    : AppThemeMode.Light;
                AppVersion = $"VibrantbitLauncher {GetAssemblyVersion()}";
                // 显示绝对路径：配置里存的是相对路径 "./.minecraft"，直接展开展示更直观。
                // 这里绕开属性 setter（不触发保存），初始值只用于展示，用户没改就不回写。
                _minecraftFolder = SettingsService.ResolveMinecraftFolder();
                OnPropertyChanged(nameof(MinecraftFolder));

                UseAccentGradient = SettingsService.Current.UseAccentGradient;

                _selectedAccent = AccentPresets.FirstOrDefault(p =>
                    string.Equals(p.Primary, SettingsService.Current.AccentColor, StringComparison.OrdinalIgnoreCase))
                    ?? AccentPresets[0];
                _selectedGradientEnd = AccentPresets.FirstOrDefault(p =>
                    string.Equals(p.GradientEnd, SettingsService.Current.AccentColorEnd, StringComparison.OrdinalIgnoreCase))
                    ?? AccentPresets[0];

                OnPropertyChanged(nameof(SelectedAccent));
                OnPropertyChanged(nameof(SelectedGradientEnd));
                UpdatePresetSelection();

                BackgroundImagePath = SettingsService.Current.BackgroundImagePath ?? string.Empty;
                BackgroundImageOpacity = SettingsService.Current.BackgroundImageOpacity;

                _performanceMode = SettingsService.Current.PerformanceMode;
                OnPropertyChanged(nameof(PerformanceMode));
                OnPropertyChanged(nameof(PerformanceModeHint));

                // 启动 / 网络两组设置：回填控件状态时同样只写字段 + 通知，不走 setter
                // （走 setter 会逐个落盘，还会在初始化阶段就把下载设置重刷一遍）
                _suppressSettingApply = true;
                try
                {
                    _selectedMaxMemory = MemoryOptions.FirstOrDefault(o => o.Mb == SettingsService.Current.MaxMemoryMb)
                        ?? MemoryOptions[2];
                    OnPropertyChanged(nameof(SelectedMaxMemory));

                    _selectedMinMemory = MemoryOptions.FirstOrDefault(o => o.Mb == SettingsService.Current.MinMemoryMb)
                        ?? MemoryOptions[1];
                    OnPropertyChanged(nameof(SelectedMinMemory));
                    OnPropertyChanged(nameof(MemoryHint));

                    _jvmArgs = SettingsService.Current.JvmArgs ?? string.Empty;
                    OnPropertyChanged(nameof(JvmArgs));

                    _versionIsolation = SettingsService.Current.VersionIsolation;
                    OnPropertyChanged(nameof(VersionIsolation));

                    _disableIpv6 = SettingsService.Current.DisableIpv6;
                    OnPropertyChanged(nameof(DisableIpv6));

                    _selectedGcMode = GcModeOptions.FirstOrDefault(o => o.Key == SettingsService.Current.GcMode)
                        ?? GcModeOptions[0];
                    OnPropertyChanged(nameof(SelectedGcMode));

                    _windowWidth = SettingsService.Current.WindowWidth;
                    OnPropertyChanged(nameof(WindowWidth));

                    _windowHeight = SettingsService.Current.WindowHeight;
                    OnPropertyChanged(nameof(WindowHeight));

                    _startFullscreen = SettingsService.Current.StartFullscreen;
                    OnPropertyChanged(nameof(StartFullscreen));

                    _selectedAfterLaunchAction =
                        AfterLaunchOptions.FirstOrDefault(o => o.Key == SettingsService.Current.AfterLaunchAction)
                        ?? AfterLaunchOptions[0];
                    OnPropertyChanged(nameof(SelectedAfterLaunchAction));

                    _useBmclMirror = SettingsService.Current.UseBmclMirror;
                    OnPropertyChanged(nameof(UseBmclMirror));
                    OnPropertyChanged(nameof(DownloadSourceText));

                    _downloadConcurrency = DownloadConcurrencyOptions.Contains(SettingsService.Current.DownloadConcurrency)
                        ? SettingsService.Current.DownloadConcurrency
                        : 6;
                    OnPropertyChanged(nameof(DownloadConcurrency));

                    _downloadChunkCount = DownloadChunkOptions.Contains(SettingsService.Current.DownloadChunkCount)
                        ? SettingsService.Current.DownloadChunkCount
                        : 4;
                    OnPropertyChanged(nameof(DownloadChunkCount));

                    _downloadRetryCount = DownloadRetryOptions.Contains(SettingsService.Current.DownloadRetryCount)
                        ? SettingsService.Current.DownloadRetryCount
                        : 4;
                    OnPropertyChanged(nameof(DownloadRetryCount));
                }
                finally
                {
                    _suppressSettingApply = false;
                }

                OnPropertyChanged(nameof(MachineMemoryText));

                // 日志设置：回填控件状态时不触发保存 / 重建
                _suppressLogApply = true;
                try
                {
                    _selectedLogLevel = LogService.AvailableLevels.FirstOrDefault(l =>
                        string.Equals(l.Key, SettingsService.Current.LogLevel, StringComparison.OrdinalIgnoreCase))
                        ?? LogService.AvailableLevels[2];
                    OnPropertyChanged(nameof(SelectedLogLevel));

                    _selectedLogRetention = LogRetentionOptions.Contains(SettingsService.Current.LogRetentionDays)
                        ? SettingsService.Current.LogRetentionDays
                        : 7;
                    OnPropertyChanged(nameof(SelectedLogRetention));

                    _logToDebugOutput = SettingsService.Current.LogToDebugOutput;
                    OnPropertyChanged(nameof(LogToDebugOutput));
                }
                finally
                {
                    _suppressLogApply = false;
                }

                FillExtendedSettings();
                LoadFontFamilies();

                RefreshLogs();

                _ = LoadJavaAsync();
            }
            finally
            {
                _suppressAppearanceApply = false;
            }

            _isInitialized = true;
        }

        private async Task LoadJavaAsync()
        {
            try
            {
                // 用自带实现而非 JavaUtil.EnumerableJavaAsync()：后者遇到空路径候选会整体抛异常。
                // 取**全部安装**（不按主版本合并）—— 这个下拉框就是给用户挑的，
                // 合并后会出现「装了 jdk-21.0.12 却只显示 jdk-21.0.3」的情况。
                var javas = await JavaHelper.FindAllJavasAsync();

                JavaEntries.Clear();
                foreach (var j in javas)
                    JavaEntries.Add(j);

                Serilog.Log.Information("Java 查找：{Count} 个 → {List}", javas.Count, string.Join(" | ", javas.Select(j => $"{j.JavaVersion} ({j.JavaPath})")));

                var saved = MainWindowViewModel.MainModel.JavaPath;
                SelectedJava = !string.IsNullOrEmpty(saved)
                    ? JavaEntries.FirstOrDefault(j => j.JavaPath == saved)
                    : JavaEntries.FirstOrDefault();

                // 配置里的路径不在检测结果里 → 说明是手动填的，把「手动指定」打开，
                // 否则界面上会显示成「自动检测」，用户改回自动时也找不到那个路径。
                var manual = !string.IsNullOrWhiteSpace(saved) && JavaEntries.All(j => j.JavaPath != saved);
                if (manual != _useManualJava)
                {
                    _useManualJava = manual;
                    OnPropertyChanged(nameof(UseManualJava));
                }

                if (manual)
                {
                    _manualJavaPath = saved!;
                    OnPropertyChanged(nameof(ManualJavaPath));
                }
            }
            catch
            {
                // 枚举失败时静默
            }
        }

        /// <summary>打开当前的 .minecraft 目录；目录不存在就先建出来，避免资源管理器报错定位不到。</summary>
        private void OpenGameFolder()
        {
            try
            {
                var folder = Path.GetFullPath(MinecraftFolder);
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "打开游戏目录失败：{Folder}", MinecraftFolder);
            }
        }

        // ===== 属性 =====

        public string AppVersion
        {
            get => _appVersion;
            set => SetProperty(ref _appVersion, value);
        }

        public AppThemeMode CurrentTheme
        {
            get => _currentTheme;
            set
            {
                if (SetProperty(ref _currentTheme, value))
                    OnPropertyChanged(nameof(CurrentThemeText));
            }
        }

        /// <summary>当前主题的中文名，供概览面板直接显示（替代 WPF 的 DataTrigger）。</summary>
        public string CurrentThemeText => _currentTheme == AppThemeMode.Dark ? "暗色" : "亮色";

        public string MinecraftFolder
        {
            get => _minecraftFolder;
            set
            {
                if (SetProperty(ref _minecraftFolder, value))
                {
                    MainWindowViewModel.MainModel.MinecraftFolder = value;
                    SettingsService.Current.MinecraftFolder = value;
                    SettingsService.Save();
                }
            }
        }

        public ObservableCollection<JavaEntry> JavaEntries => _javaEntries;

        public JavaEntry? SelectedJava
        {
            get => _selectedJava;
            set
            {
                if (SetProperty(ref _selectedJava, value) && value != null)
                {
                    MainWindowViewModel.MainModel.JavaPath = value.JavaPath;
                    MainWindowViewModel.MainModel.Java = value;
                    SettingsService.Current.JavaPath = value.JavaPath;
                    SettingsService.Save();
                }

                // 概览里的「Java 路径」优先显示配置值，选中项变化后要重算
                OnPropertyChanged(nameof(JavaPathText));
            }
        }

        /// <summary>
        /// 「主页」概览里展示的 Java 路径。
        ///
        /// <para>
        /// 优先用<b>配置里的值</b>，而不是下拉框选中项：检测列表里没有这个 JDK 时
        /// （手动填的路径、目录被移走、刚升级把旧目录删了），选中项会退回到列表第一项，
        /// 概览就会显示成另一个 JDK —— 用户会以为配置被自己改了。
        /// </para>
        /// </summary>
        public string JavaPathText =>
            !string.IsNullOrWhiteSpace(MainWindowViewModel.MainModel.JavaPath)
                ? MainWindowViewModel.MainModel.JavaPath!
                : _selectedJava?.JavaPath ?? "未设置";

        // ===== 启动：Java 虚拟机与内存 / 游戏目录 / 高级 =====

        /// <summary>可选最大 / 初始堆内存档位。上限不设到 64G 之外，避免手滑把整机内存吃掉。</summary>
        public static readonly IReadOnlyList<MemoryOption> MemoryOptions = new[]
        {
            new MemoryOption(512, "512 MB"),
            new MemoryOption(1024, "1 GB"),
            new MemoryOption(2048, "2 GB"),
            new MemoryOption(3072, "3 GB"),
            new MemoryOption(4096, "4 GB"),
            new MemoryOption(6144, "6 GB"),
            new MemoryOption(8192, "8 GB"),
            new MemoryOption(12288, "12 GB"),
            new MemoryOption(16384, "16 GB"),
            new MemoryOption(32768, "32 GB"),
            new MemoryOption(65536, "64 GB"),
        };

        /// <summary>垃圾回收器。Default 表示不额外指定，交给 JVM 按版本自己选。</summary>
        public static readonly IReadOnlyList<SettingOption> GcModeOptions = new[]
        {
            new SettingOption("Default", "自动（JVM 默认）"),
            new SettingOption("G1GC", "G1GC · 通用推荐"),
            new SettingOption("ZGC", "ZGC · 大内存低停顿"),
            new SettingOption("Parallel", "ParallelGC · 老版本友好"),
            new SettingOption("Serial", "SerialGC · 单核 / 小内存"),
        };

        /// <summary>启动游戏后对启动器自身做什么。</summary>
        public static readonly IReadOnlyList<SettingOption> AfterLaunchOptions = new[]
        {
            new SettingOption("None", "不做任何操作"),
            new SettingOption("Minimize", "最小化启动器"),
            new SettingOption("Close", "关闭启动器"),
        };

        /// <summary>
        /// 「同时下载文件数」的档位。
        ///
        /// 上限 16：再高对家用带宽没有收益，而且总连接数 = 并发 × 分块，
        /// 16 × 16 已经 256 条连接，镜像端（尤其 BMCLAPI）会直接限流。
        /// </summary>
        public static readonly IReadOnlyList<int> DownloadConcurrencyOptions =
            new[] { 1, 2, 3, 4, 6, 8, 10, 12, 16 };

        /// <summary>「单文件分块数」的档位。1 = 不分块、单连接。</summary>
        public static readonly IReadOnlyList<int> DownloadChunkOptions =
            new[] { 1, 2, 3, 4, 6, 8, 12, 16 };

        /// <summary>下载失败重试次数档位。</summary>
        public static readonly IReadOnlyList<int> DownloadRetryOptions = new[] { 0, 1, 2, 3, 4, 6, 8, 10 };

        public IReadOnlyList<MemoryOption> MaxMemoryOptions => MemoryOptions;
        public IReadOnlyList<MemoryOption> MinMemoryOptions => MemoryOptions;
        public IReadOnlyList<SettingOption> GcModes => GcModeOptions;
        public IReadOnlyList<SettingOption> AfterLaunchActions => AfterLaunchOptions;
        public IReadOnlyList<int> DownloadConcurrencyOptionsList => DownloadConcurrencyOptions;
        public IReadOnlyList<int> DownloadChunkOptionsList => DownloadChunkOptions;
        public IReadOnlyList<int> DownloadRetryOptionsList => DownloadRetryOptions;

        /// <summary>本机可用物理内存的粗略值（取自 GC 对进程可见的内存上限），用于给出建议档位。</summary>
        public string MachineMemoryText
        {
            get
            {
                try
                {
                    var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                    if (bytes <= 0)
                        return string.Empty;

                    return $"本机内存约 {bytes / 1024.0 / 1024 / 1024:F1} GB";
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>最大堆内存。改动即落盘，下次启动游戏生效。</summary>
        public MemoryOption SelectedMaxMemory
        {
            get => _selectedMaxMemory;
            set
            {
                if (value == null || !SetProperty(ref _selectedMaxMemory, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.MaxMemoryMb = value.Mb;
                SettingsService.Save();
                OnPropertyChanged(nameof(MemoryHint));
                Serilog.Log.Information("最大内存已设为 {Mb} MB", value.Mb);
            }
        }

        /// <summary>初始堆内存。</summary>
        public MemoryOption SelectedMinMemory
        {
            get => _selectedMinMemory;
            set
            {
                if (value == null || !SetProperty(ref _selectedMinMemory, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.MinMemoryMb = value.Mb;
                SettingsService.Save();
                OnPropertyChanged(nameof(MemoryHint));
                Serilog.Log.Information("初始内存已设为 {Mb} MB", value.Mb);
            }
        }

        /// <summary>内存档位的一句提醒（初始值高于最大值时是无效配置，要提前告诉用户）。</summary>
        public string MemoryHint =>
            _selectedMinMemory.Mb > _selectedMaxMemory.Mb
                ? "初始内存高于最大内存，启动时会自动按最大内存处理"
                : $"启动参数：-Xmx{_selectedMaxMemory.Mb}m -Xms{_selectedMinMemory.Mb}m";

        /// <summary>自定义 JVM 参数，一行一条。</summary>
        public string JvmArgs
        {
            get => _jvmArgs;
            set
            {
                if (!SetProperty(ref _jvmArgs, value ?? string.Empty) || _suppressSettingApply)
                    return;

                SettingsService.Current.JvmArgs = _jvmArgs;
                SettingsService.Save();
            }
        }

        /// <summary>版本隔离：每个版本使用独立的 saves / mods / config 目录。</summary>
        public bool VersionIsolation
        {
            get => _versionIsolation;
            set
            {
                if (!SetProperty(ref _versionIsolation, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.VersionIsolation = value;
                SettingsService.Save();
                Serilog.Log.Information("版本隔离：{Enabled}", value ? "开" : "关");
            }
        }

        /// <summary>禁用 IPv6（-Djava.net.preferIPv4Stack=true）。</summary>
        public bool DisableIpv6
        {
            get => _disableIpv6;
            set
            {
                if (!SetProperty(ref _disableIpv6, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.DisableIpv6 = value;
                SettingsService.Save();
                Serilog.Log.Information("禁用 IPv6：{Enabled}", value ? "开" : "关");
            }
        }

        /// <summary>垃圾回收器。</summary>
        public SettingOption SelectedGcMode
        {
            get => _selectedGcMode;
            set
            {
                if (value == null || !SetProperty(ref _selectedGcMode, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.GcMode = value.Key;
                SettingsService.Save();
                Serilog.Log.Information("GC 模式已设为 {Mode}", value.Key);
            }
        }

        /// <summary>游戏窗口宽度。</summary>
        public int WindowWidth
        {
            get => _windowWidth;
            set
            {
                if (!SetProperty(ref _windowWidth, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.WindowWidth = value;
                SettingsService.Save();
            }
        }

        /// <summary>游戏窗口高度。</summary>
        public int WindowHeight
        {
            get => _windowHeight;
            set
            {
                if (!SetProperty(ref _windowHeight, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.WindowHeight = value;
                SettingsService.Save();
            }
        }

        /// <summary>是否全屏启动（开启后忽略窗体宽高）。</summary>
        public bool StartFullscreen
        {
            get => _startFullscreen;
            set
            {
                if (!SetProperty(ref _startFullscreen, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.StartFullscreen = value;
                SettingsService.Save();
            }
        }

        /// <summary>启动游戏后对启动器自身执行的动作。</summary>
        public SettingOption SelectedAfterLaunchAction
        {
            get => _selectedAfterLaunchAction;
            set
            {
                if (value == null || !SetProperty(ref _selectedAfterLaunchAction, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.AfterLaunchAction = value.Key;
                SettingsService.Save();
                Serilog.Log.Information("启动后行为已设为 {Action}", value.Key);
            }
        }

        // ===== 网络：下载 =====

        /// <summary>使用 BMCLAPI 镜像（国内加速）。</summary>
        public bool UseBmclMirror
        {
            get => _useBmclMirror;
            set
            {
                if (!SetProperty(ref _useBmclMirror, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.UseBmclMirror = value;
                SettingsService.Save();
                OnPropertyChanged(nameof(DownloadSourceText));
                OnPropertyChanged(nameof(DownloadSourceSummary));

                // 立即生效：镜像开关会马上影响后续所有下载的 URL 改写
                SettingsService.ApplyDownloadSettings();
            }
        }

        /// <summary>同时下载几个文件（文件级并发）。</summary>
        public int DownloadConcurrency
        {
            get => _downloadConcurrency;
            set
            {
                if (!SetProperty(ref _downloadConcurrency, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.DownloadConcurrency = value;
                SettingsService.Save();
                OnPropertyChanged(nameof(DownloadSourceSummary));
                Serilog.Log.Information("同时下载文件数已设为 {Count}", value);
            }
        }

        /// <summary>单个文件切成几块并行下载（1 = 不分块）。</summary>
        public int DownloadChunkCount
        {
            get => _downloadChunkCount;
            set
            {
                if (!SetProperty(ref _downloadChunkCount, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.DownloadChunkCount = value;
                SettingsService.Save();
                OnPropertyChanged(nameof(DownloadSourceSummary));
                Serilog.Log.Information("单文件分块数已设为 {Count}", value);
            }
        }

        /// <summary>下载失败重试次数。</summary>
        public int DownloadRetryCount
        {
            get => _downloadRetryCount;
            set
            {
                if (!SetProperty(ref _downloadRetryCount, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.DownloadRetryCount = value;
                SettingsService.Save();
                SettingsService.ApplyDownloadSettings();
            }
        }

        /// <summary>下载源的一句话说明。</summary>
        public string DownloadSourceText =>
            _useBmclMirror
                ? "BMCLAPI 镜像：国内节点，速度通常更快，但更新可能略滞后于官方"
                : "官方源：直接连 Mojang，版本信息最新，国内访问可能较慢";

        /// <summary>概览面板用的下载源摘要。</summary>
        public string DownloadSourceSummary =>
            $"{(_useBmclMirror ? "BMCLAPI 镜像" : "官方源")} · {_downloadConcurrency} 文件并发 · {_downloadChunkCount} 块/文件";

        // ===== 个性化：主题色 / 渐变色 / 背景图 =====

        private readonly ObservableCollection<AccentPreset> _accentPresets = new()
        {
            new AccentPreset("默认蓝", "#FF0078D4", "#FF00B7C3"),
            new AccentPreset("海洋青", "#FF0F6E56", "#FF5DCAA5"),
            new AccentPreset("森林绿", "#FF3B6D11", "#FF97C459"),
            new AccentPreset("紫罗兰", "#FF534AB7", "#FFAFA9EC"),
            new AccentPreset("玫瑰粉", "#FF993556", "#FFED93B1"),
            new AccentPreset("落日橙", "#FFBA7517", "#FFEF9F27"),
            new AccentPreset("石榴红", "#FFA32D2D", "#FFE24B4A"),
            new AccentPreset("靛青蓝", "#FF185FA5", "#FF85B7EB"),
        };

        private readonly ObservableCollection<WallpaperOption> _wallpapers = new()
        {
            new WallpaperOption("草地", "/Assets/bg1.png"),
            new WallpaperOption("暮色", "/Assets/bg2.png"),
            new WallpaperOption("峡谷", "/Assets/bg3.png"),
            new WallpaperOption("雪原", "/Assets/bg4.png"),
        };

        private AccentPreset? _selectedAccent;
        private AccentPreset? _selectedGradientEnd;
        private bool _useAccentGradient;
        private bool _suppressAppearanceApply;
        private string _backgroundImagePath = string.Empty;
        private double _backgroundImageOpacity = 0.85;
        private bool _performanceMode;

        /// <summary>可选主题色。</summary>
        public ObservableCollection<AccentPreset> AccentPresets => _accentPresets;

        /// <summary>内置壁纸。</summary>
        public ObservableCollection<WallpaperOption> Wallpapers => _wallpapers;

        /// <summary>当前主色调。</summary>
        public AccentPreset? SelectedAccent => _selectedAccent;

        /// <summary>当前渐变终点色，仅在开启「主题渐变色」时参与显示。</summary>
        public AccentPreset? SelectedGradientEnd => _selectedGradientEnd;

        /// <summary>是否启用主题渐变色。</summary>
        public bool UseAccentGradient
        {
            get => _useAccentGradient;
            set
            {
                if (SetProperty(ref _useAccentGradient, value) && !_suppressAppearanceApply)
                    ApplyAppearanceSettings();
            }
        }

        /// <summary>当前背景图路径（本地绝对路径或内置壁纸 pack URI），空表示未设置。</summary>
        public string BackgroundImagePath
        {
            get => _backgroundImagePath;
            private set
            {
                if (SetProperty(ref _backgroundImagePath, value))
                {
                    OnPropertyChanged(nameof(HasBackgroundImage));
                    OnPropertyChanged(nameof(BackgroundImageDisplay));
                }
            }
        }

        public bool HasBackgroundImage => !string.IsNullOrWhiteSpace(_backgroundImagePath);

        /// <summary>背景图在界面上显示的名字。</summary>
        public string BackgroundImageDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_backgroundImagePath))
                    return "未设置（使用系统 Mica 质感底）";

                var builtin = _wallpapers.FirstOrDefault(w =>
                    string.Equals(w.ImagePath, _backgroundImagePath, StringComparison.OrdinalIgnoreCase));

                return builtin != null ? $"内置壁纸 · {builtin.Name}" : Path.GetFileName(_backgroundImagePath);
            }
        }

        /// <summary>背景图不透明度，拖动即时预览。</summary>
        public double BackgroundImageOpacity
        {
            get => _backgroundImageOpacity;
            set
            {
                if (SetProperty(ref _backgroundImageOpacity, value) && !_suppressAppearanceApply)
                {
                    SettingsService.Current.BackgroundImageOpacity = value;
                    SettingsService.ApplyBackground();
                    SettingsService.Save();
                }
            }
        }

        /// <summary>
        /// 性能模式开关：关闭云母材质 / 壁纸 / 动效 / 阴影，并改用软件渲染。
        ///
        /// 立即生效的部分（动效、壁纸、Mica 判定）走 <see cref="SettingsService.ApplyPerformanceMode"/>；
        /// 渲染后端与窗口类名这类只在启动时决定的东西需要重启，界面上有明确提示。
        /// </summary>
        public bool PerformanceMode
        {
            get => _performanceMode;
            set
            {
                if (!SetProperty(ref _performanceMode, value) || _suppressAppearanceApply)
                    return;

                SettingsService.Current.PerformanceMode = value;
                SettingsService.ApplyPerformanceMode();
                SettingsService.ApplyBackground();
                SettingsService.Save();
                RefreshWindowMotion();

                OnPropertyChanged(nameof(PerformanceModeHint));
            }
        }

        /// <summary>性能模式的状态说明（渲染方式要重启才切换，这里如实告诉用户）。</summary>
        public string PerformanceModeHint
            => _performanceMode
                ? "已开启：动效 / 阴影 / 壁纸已关闭。重启启动器后渲染方式也会切到软件渲染。"
                : "为低配电脑（尤其 Windows 7 老机器）关闭动效、阴影与壁纸，并改用软件渲染。";

        public RelayCommand<WallpaperOption> PickWallpaperCommand { get; }
        public RelayCommand ClearBackgroundCommand { get; }
        public RelayCommand ResetAppearanceCommand { get; }
        public RelayCommand<AccentPreset> SelectAccentCommand { get; }
        public RelayCommand<AccentPreset> SelectGradientEndCommand { get; }

        /// <summary>点选主色调。</summary>
        private void SelectAccent(AccentPreset? preset)
        {
            if (preset == null || ReferenceEquals(preset, _selectedAccent))
                return;

            _selectedAccent = preset;
            OnPropertyChanged(nameof(SelectedAccent));
            UpdatePresetSelection();
            ApplyAppearanceSettings();
        }

        /// <summary>点选渐变终点色。</summary>
        private void SelectGradientEnd(AccentPreset? preset)
        {
            if (preset == null || ReferenceEquals(preset, _selectedGradientEnd))
                return;

            _selectedGradientEnd = preset;
            OnPropertyChanged(nameof(SelectedGradientEnd));
            UpdatePresetSelection();
            ApplyAppearanceSettings();
        }

        /// <summary>把选中状态同步到各预设，供色板画出选中环。</summary>
        private void UpdatePresetSelection()
        {
            foreach (var preset in _accentPresets)
            {
                preset.IsAccentSelected = ReferenceEquals(preset, _selectedAccent);
                preset.IsGradientSelected = ReferenceEquals(preset, _selectedGradientEnd);
            }
        }

        /// <summary>把当前的主题色 / 渐变配置写回设置并立即应用。</summary>
        private void ApplyAppearanceSettings()
        {
            if (SelectedAccent != null)
                SettingsService.Current.AccentColor = SelectedAccent.Primary;
            if (SelectedGradientEnd != null)
                SettingsService.Current.AccentColorEnd = SelectedGradientEnd.GradientEnd;
            SettingsService.Current.UseAccentGradient = UseAccentGradient;

            // 走「刷新主题」入口：只改 SystemAccentColor 不会刷新主题词典里固化的强调色资源
            SettingsService.ApplyAccentChange();
            SettingsService.Save();
        }

        private void PickWallpaper(WallpaperOption? option)
        {
            if (option != null)
                SetBackgroundImage(option.ImagePath);
        }

        /// <summary>设置背景图（本地绝对路径或内置壁纸 pack URI）；传空字符串表示清除。</summary>
        public void SetBackgroundImage(string? path)
        {
            BackgroundImagePath = path ?? string.Empty;
            SettingsService.Current.BackgroundImagePath = BackgroundImagePath;
            SettingsService.ApplyBackground();
            SettingsService.Save();
        }

        private void ClearBackground() => SetBackgroundImage(string.Empty);

        /// <summary>恢复默认外观：默认蓝 + 关闭渐变 + 移除背景图。</summary>
        private void ResetAppearance()
        {
            _suppressAppearanceApply = true;
            try
            {
                _useAccentGradient = false;
                OnPropertyChanged(nameof(UseAccentGradient));
                _selectedAccent = _accentPresets[0];
                OnPropertyChanged(nameof(SelectedAccent));
                _selectedGradientEnd = _accentPresets[0];
                OnPropertyChanged(nameof(SelectedGradientEnd));
                BackgroundImagePath = string.Empty;
                _backgroundImageOpacity = 0.85;
                OnPropertyChanged(nameof(BackgroundImageOpacity));
                UpdatePresetSelection();
            }
            finally
            {
                _suppressAppearanceApply = false;
            }

            SettingsService.Current.UseAccentGradient = false;
            SettingsService.Current.AccentColor = _accentPresets[0].Primary;
            SettingsService.Current.AccentColorEnd = _accentPresets[0].GradientEnd;
            SettingsService.Current.BackgroundImagePath = string.Empty;
            SettingsService.Current.BackgroundImageOpacity = 0.85;

            SettingsService.ApplyAccentChange();
            SettingsService.ApplyBackground();
            SettingsService.Save();
        }

        // ===== 通用 =====

        private bool _autoLoginLastAccount = true;
        private bool _minimizeToTray = true;
        private bool _updateIncludePrerelease;
        private string _updateStatusText = string.Empty;
        private string _currentAccountName = "未登录";
        private string _currentAccountTypeText = "在「账户」页登录后即可启动游戏";
        private string _currentAccountAvatar = string.Empty;
        private bool _hasAccount;

        private bool _showHomeGreeting = true;
        private bool _showHomeLaunchCard = true;
        private bool _showHomeRecent = true;
        private bool _showHomeInstanceOverview = true;
        private bool _showHomeShortcuts = true;

        /// <summary>启动时自动恢复上次登录的账户。</summary>
        public bool AutoLoginLastAccount
        {
            get => _autoLoginLastAccount;
            set
            {
                if (!SetProperty(ref _autoLoginLastAccount, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.AutoLoginLastAccount = value;
                SettingsService.Save();
            }
        }

        /// <summary>点窗口关闭按钮时收进系统托盘。</summary>
        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set
            {
                if (!SetProperty(ref _minimizeToTray, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.MinimizeToTray = value;
                SettingsService.Save();
            }
        }

        /// <summary>检查更新时是否包含测试版。</summary>
        public bool UpdateIncludePrerelease
        {
            get => _updateIncludePrerelease;
            set
            {
                if (!SetProperty(ref _updateIncludePrerelease, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.UpdateIncludePrerelease = value;
                SettingsService.Save();
            }
        }

        /// <summary>上一次检查更新的结果说明。</summary>
        public string UpdateStatusText
        {
            get => _updateStatusText;
            private set => SetProperty(ref _updateStatusText, value);
        }

        public string CurrentAccountName
        {
            get => _currentAccountName;
            private set => SetProperty(ref _currentAccountName, value);
        }

        public string CurrentAccountTypeText
        {
            get => _currentAccountTypeText;
            private set => SetProperty(ref _currentAccountTypeText, value);
        }

        public string CurrentAccountAvatar
        {
            get => _currentAccountAvatar;
            private set => SetProperty(ref _currentAccountAvatar, value);
        }

        public bool HasAccount
        {
            get => _hasAccount;
            private set => SetProperty(ref _hasAccount, value);
        }

        public bool ShowHomeGreeting
        {
            get => _showHomeGreeting;
            set => SetHomeFlag(ref _showHomeGreeting, value,
                v => SettingsService.Current.ShowHomeGreeting = v, nameof(ShowHomeGreeting));
        }

        public bool ShowHomeLaunchCard
        {
            get => _showHomeLaunchCard;
            set => SetHomeFlag(ref _showHomeLaunchCard, value,
                v => SettingsService.Current.ShowHomeLaunchCard = v, nameof(ShowHomeLaunchCard));
        }

        public bool ShowHomeRecent
        {
            get => _showHomeRecent;
            set => SetHomeFlag(ref _showHomeRecent, value,
                v => SettingsService.Current.ShowHomeRecent = v, nameof(ShowHomeRecent));
        }

        public bool ShowHomeInstanceOverview
        {
            get => _showHomeInstanceOverview;
            set => SetHomeFlag(ref _showHomeInstanceOverview, value,
                v => SettingsService.Current.ShowHomeInstanceOverview = v, nameof(ShowHomeInstanceOverview));
        }

        public bool ShowHomeShortcuts
        {
            get => _showHomeShortcuts;
            set => SetHomeFlag(ref _showHomeShortcuts, value,
                v => SettingsService.Current.ShowHomeShortcuts = v, nameof(ShowHomeShortcuts));
        }

        private void SetHomeFlag(ref bool field, bool value, Action<bool> write, string propertyName)
        {
            if (!SetProperty(ref field, value, propertyName) || _suppressSettingApply)
                return;

            write(value);
            SettingsService.Save();
        }

        // ===== 游戏 · Java =====

        private bool _useManualJava;
        private string _manualJavaPath = string.Empty;

        /// <summary>是否使用手动指定的 Java（而不是从检测列表里挑）。</summary>
        public bool UseManualJava
        {
            get => _useManualJava;
            set
            {
                if (!SetProperty(ref _useManualJava, value) || _suppressSettingApply)
                    return;

                if (value)
                {
                    // 打开时把当前生效的路径填进输入框，用户只需改不对的那一段
                    ManualJavaPath = MainWindowViewModel.MainModel.JavaPath ?? string.Empty;
                }
                else
                {
                    // 关掉时退回「自动检测」—— 不能把路径清空，
                    // 否则下次启动会因为找不到 Java 而报错，用户还以为设置坏了。
                    SelectedJava = JavaEntries.FirstOrDefault();
                }
            }
        }

        /// <summary>手动指定的 java 可执行文件路径。</summary>
        public string ManualJavaPath
        {
            get => _manualJavaPath;
            set
            {
                if (!SetProperty(ref _manualJavaPath, value ?? string.Empty) || _suppressSettingApply)
                    return;

                if (!_useManualJava || string.IsNullOrWhiteSpace(_manualJavaPath))
                    return;

                MainWindowViewModel.MainModel.JavaPath = _manualJavaPath;
                SettingsService.Current.JavaPath = _manualJavaPath;
                SettingsService.Save();
                OnPropertyChanged(nameof(JavaPathText));
            }
        }

        // ===== 游戏 · 内存滑块 =====

        private double _maxMemoryMbValue = 2048;

        /// <summary>
        /// 最大内存的滑块值（MB）。
        ///
        /// 直接对着 MB 拖，而不是从固定档位里选：档位跨度太大（4G 到 6G 之间就断了），
        /// 8G 机器想给 5G 只能二选一。落盘时仍按整数 MB 记，和旧的档位配置天然兼容。
        /// </summary>
        public double MaxMemoryMbValue
        {
            get => _maxMemoryMbValue;
            set
            {
                var rounded = Math.Round(value / 256) * 256;   // 256 MB 一档，拖动时手感更稳
                if (!SetProperty(ref _maxMemoryMbValue, rounded))
                    return;

                OnPropertyChanged(nameof(MaxMemorySliderText));

                if (_suppressSettingApply)
                    return;

                SettingsService.Current.MaxMemoryMb = (int)rounded;
                SettingsService.Save();

                // 弹窗/概览用的是档位对象，滑条改了要一起对齐，否则 -Xmx 提示还是旧值
                _selectedMaxMemory = MemoryOptions.LastOrDefault(o => o.Mb <= (int)rounded) ?? MemoryOptions[0];
                OnPropertyChanged(nameof(SelectedMaxMemory));
                OnPropertyChanged(nameof(MemoryHint));

                // 拖滑块等于手动指定，自动推荐的勾要自己掉下来
                if (_memoryAutoRecommend)
                {
                    _memoryAutoRecommend = false;
                    OnPropertyChanged(nameof(MemoryAutoRecommend));
                    SettingsService.Current.MemoryAutoRecommend = false;
                    SettingsService.Save();
                }
            }
        }

        public string MaxMemorySliderText => $"{_maxMemoryMbValue / 1024:0.#} GB（{_maxMemoryMbValue:0} MB）";

        private bool _memoryAutoRecommend = true;

        /// <summary>内存是否按本机容量自动推荐。</summary>
        public bool MemoryAutoRecommend
        {
            get => _memoryAutoRecommend;
            set
            {
                if (!SetProperty(ref _memoryAutoRecommend, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.MemoryAutoRecommend = value;
                SettingsService.Save();

                if (value)
                    AutoConfigureMemory();
            }
        }

        // ===== 游戏 · 高级参数 =====

        private bool _useCustomGameArgs;
        private string _gameArgs = string.Empty;
        private string _resourcePacksFolder = string.Empty;
        private string _savesFolder = string.Empty;
        private bool _crashReportAutoUpload;

        /// <summary>是否追加自定义游戏参数。</summary>
        public bool UseCustomGameArgs
        {
            get => _useCustomGameArgs;
            set
            {
                if (!SetProperty(ref _useCustomGameArgs, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.UseCustomGameArgs = value;
                SettingsService.Save();
            }
        }

        /// <summary>自定义游戏参数，一行一条。</summary>
        public string GameArgs
        {
            get => _gameArgs;
            set
            {
                if (!SetProperty(ref _gameArgs, value ?? string.Empty) || _suppressSettingApply)
                    return;

                SettingsService.Current.GameArgs = _gameArgs;
                SettingsService.Save();
            }
        }

        /// <summary>资源包目录的自定义位置（空 = 游戏目录下的 resourcepacks）。</summary>
        public string ResourcePacksFolder
        {
            get => _resourcePacksFolder;
            set
            {
                if (!SetProperty(ref _resourcePacksFolder, value ?? string.Empty) || _suppressSettingApply)
                    return;

                SettingsService.Current.ResourcePacksFolder = _resourcePacksFolder;
                SettingsService.Save();
                OnPropertyChanged(nameof(ResourcePacksFolderDisplay));
            }
        }

        /// <summary>存档目录的自定义位置（空 = 游戏目录下的 saves）。</summary>
        public string SavesFolder
        {
            get => _savesFolder;
            set
            {
                if (!SetProperty(ref _savesFolder, value ?? string.Empty) || _suppressSettingApply)
                    return;

                SettingsService.Current.SavesFolder = _savesFolder;
                SettingsService.Save();
                OnPropertyChanged(nameof(SavesFolderDisplay));
            }
        }

        public string ResourcePacksFolderDisplay => string.IsNullOrWhiteSpace(_resourcePacksFolder)
            ? $"默认：{Path.Combine(MinecraftFolder, "resourcepacks")}"
            : _resourcePacksFolder;

        public string SavesFolderDisplay => string.IsNullOrWhiteSpace(_savesFolder)
            ? $"默认：{Path.Combine(MinecraftFolder, "saves")}"
            : _savesFolder;

        /// <summary>崩溃日志自动上传（目前只记本地，开关位留给后续接入）。</summary>
        public bool CrashReportAutoUpload
        {
            get => _crashReportAutoUpload;
            set
            {
                if (!SetProperty(ref _crashReportAutoUpload, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.CrashReportAutoUpload = value;
                SettingsService.Save();
            }
        }

        // ===== 外观 · 主题 =====

        private SettingOption _selectedThemeOption = ThemeOptions[0];

        public static readonly IReadOnlyList<SettingOption> ThemeOptions = new[]
        {
            new SettingOption("Light", "亮色"),
            new SettingOption("Dark", "暗色"),
            new SettingOption("System", "跟随系统"),
        };

        public IReadOnlyList<SettingOption> ThemeOptionList => ThemeOptions;

        /// <summary>明暗主题（三态，含跟随系统）。</summary>
        public SettingOption SelectedThemeOption
        {
            get => _selectedThemeOption;
            set
            {
                if (value == null || !SetProperty(ref _selectedThemeOption, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.Theme = value.Key;
                SettingsService.ApplyTheme();
                SettingsService.ApplyHighContrast();
                SettingsService.Save();

                _currentTheme = value.Key == "Dark" ? AppThemeMode.Dark : AppThemeMode.Light;
                OnPropertyChanged(nameof(CurrentTheme));
                OnPropertyChanged(nameof(CurrentThemeText));
            }
        }

        // ===== 外观 · 自定义主题色 =====

        private double _accentR;
        private double _accentG;
        private double _accentB;

        public double AccentR
        {
            get => _accentR;
            set { if (SetProperty(ref _accentR, value)) OnAccentChannelChanged(); }
        }

        public double AccentG
        {
            get => _accentG;
            set { if (SetProperty(ref _accentG, value)) OnAccentChannelChanged(); }
        }

        public double AccentB
        {
            get => _accentB;
            set { if (SetProperty(ref _accentB, value)) OnAccentChannelChanged(); }
        }

        /// <summary>取色器的实时预览色块。</summary>
        public IBrush AccentPreviewBrush
        {
            get
            {
                var color = Color.FromRgb((byte)_accentR, (byte)_accentG, (byte)_accentB);
                return new SolidColorBrush(color);
            }
        }

        public string AccentHexText =>
            $"#{((byte)_accentR):X2}{((byte)_accentG):X2}{((byte)_accentB):X2}";

        private void OnAccentChannelChanged()
        {
            OnPropertyChanged(nameof(AccentPreviewBrush));
            OnPropertyChanged(nameof(AccentHexText));
        }

        // ===== 外观 · 背景 =====

        private SettingOption _selectedBackgroundMode = BackgroundModeOptions[0];
        private string _backgroundColorHex = "#FF12161C";
        private double _backgroundBlur;
        private double _backgroundVignette;
        private bool _backgroundAnimated;

        public static readonly IReadOnlyList<SettingOption> BackgroundModeOptions = new[]
        {
            new SettingOption("None", "无（只保留光斑）"),
            new SettingOption("Color", "纯色"),
            new SettingOption("Image", "图片 / 内置壁纸"),
        };

        public IReadOnlyList<SettingOption> BackgroundModeList => BackgroundModeOptions;

        /// <summary>背景类型：无 / 纯色 / 图片。</summary>
        public SettingOption SelectedBackgroundMode
        {
            get => _selectedBackgroundMode;
            set
            {
                if (value == null || !SetProperty(ref _selectedBackgroundMode, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.BackgroundMode = value.Key;
                SettingsService.ApplyBackground();
                SettingsService.Save();
            }
        }

        /// <summary>纯色背景的十六进制色值。</summary>
        public string BackgroundColorHex
        {
            get => _backgroundColorHex;
            set
            {
                if (!SetProperty(ref _backgroundColorHex, value ?? string.Empty))
                    return;

                OnPropertyChanged(nameof(BackgroundColorPreviewBrush));

                if (_suppressSettingApply)
                    return;

                if (!SettingsService.TryParseColor(_backgroundColorHex, out _))
                    return;

                SettingsService.Current.BackgroundColor = _backgroundColorHex;
                SettingsService.ApplyBackground();
                SettingsService.Save();
            }
        }

        public IBrush BackgroundColorPreviewBrush =>
            SettingsService.TryParseColor(_backgroundColorHex, out var color)
                ? new SolidColorBrush(color)
                : new SolidColorBrush(Colors.Transparent);

        /// <summary>毛玻璃强度 0~1（0 = 不模糊）。</summary>
        public double BackgroundBlur
        {
            get => _backgroundBlur;
            set
            {
                if (!SetProperty(ref _backgroundBlur, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.BackgroundBlur = value;
                SettingsService.ApplyBackground();
                SettingsService.Save();
            }
        }

        /// <summary>暗角强度 0~1。</summary>
        public double BackgroundVignette
        {
            get => _backgroundVignette;
            set
            {
                if (!SetProperty(ref _backgroundVignette, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.BackgroundVignette = value;
                SettingsService.ApplyBackground();
                SettingsService.Save();
            }
        }

        /// <summary>背景图是否缓慢缩放（动态背景）。</summary>
        public bool BackgroundAnimated
        {
            get => _backgroundAnimated;
            set
            {
                if (!SetProperty(ref _backgroundAnimated, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.BackgroundAnimated = value;
                SettingsService.ApplyBackground();
                SettingsService.Save();
            }
        }

        // ===== 外观 · 动效 =====

        private SettingOption _selectedAnimationLevel = AnimationLevelOptions[0];
        private SettingOption _selectedPageTransition = PageTransitionOptions[0];
        private double _animationSpeed = 1.0;

        public static readonly IReadOnlyList<SettingOption> AnimationLevelOptions = new[]
        {
            new SettingOption("Full", "完整"),
            new SettingOption("Reduced", "精简"),
            new SettingOption("Off", "关闭"),
        };

        public static readonly IReadOnlyList<SettingOption> PageTransitionOptions = new[]
        {
            new SettingOption("Slide", "滑动（上浮淡入）"),
            new SettingOption("Fade", "渐变（只改透明度）"),
            new SettingOption("None", "无（直接切换）"),
        };

        public IReadOnlyList<SettingOption> AnimationLevelList => AnimationLevelOptions;
        public IReadOnlyList<SettingOption> PageTransitionList => PageTransitionOptions;

        /// <summary>动画档位：完整 / 精简 / 关闭。</summary>
        public SettingOption SelectedAnimationLevel
        {
            get => _selectedAnimationLevel;
            set
            {
                if (value == null || !SetProperty(ref _selectedAnimationLevel, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.AnimationLevel = value.Key;
                SettingsService.ApplyMotion();
                SettingsService.Save();
                RefreshWindowMotion();
                OnPropertyChanged(nameof(AnimationLevelHint));
            }
        }

        public string AnimationLevelHint => _selectedAnimationLevel.Key switch
        {
            "Off" => "所有过渡与入场动画停止，界面变成「点了就变」。",
            "Reduced" => "保留颜色与明暗反馈，去掉位移和缩放，老机器上更跟手。",
            _ => "所有动效开启。低配电脑可切到「精简」或「关闭」。"
        };

        /// <summary>页面切换方式。</summary>
        public SettingOption SelectedPageTransition
        {
            get => _selectedPageTransition;
            set
            {
                if (value == null || !SetProperty(ref _selectedPageTransition, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.PageTransition = value.Key;
                SettingsService.ApplyMotion();
                SettingsService.Save();
            }
        }

        /// <summary>动画速度倍率 0.5（慢）~ 2（快）。</summary>
        public double AnimationSpeed
        {
            get => _animationSpeed;
            set
            {
                if (!SetProperty(ref _animationSpeed, value))
                    return;

                OnPropertyChanged(nameof(AnimationSpeedText));

                if (_suppressSettingApply)
                    return;

                SettingsService.Current.AnimationSpeed = value;
                SettingsService.ApplyMotion();
                SettingsService.Save();
            }
        }

        public string AnimationSpeedText => $"{_animationSpeed:0.0}×";
        /// <summary>
        /// 动效档位变了之后，主窗口根节点挂的类名要重挂一次。
        /// 类是本地的，改资源不会自动跟着变。
        /// </summary>
        private static void RefreshWindowMotion()
        {
            if (AppLifetime.MainWindow is Views.Windows.MainWindow window)
                window.RefreshMotionClasses();
        }

        /// <summary>把取色器调出来的主色写进配置并立即应用。</summary>
        private void ApplyCustomAccent()
        {
            var color = Color.FromRgb((byte)_accentR, (byte)_accentG, (byte)_accentB);
            SettingsService.Current.AccentColor = SettingsService.ColorToString(color);

            // 自定义色不属于任何一个预设，选中环要全部熄灭
            _selectedAccent = null;
            UpdatePresetSelection();
            OnPropertyChanged(nameof(SelectedAccent));

            SettingsService.ApplyAccentChange();
            SettingsService.Save();
            AppServices.Notifications?.Success($"主题色已应用 {AccentHexText}");
        }

        /// <summary>登出当前账户（只清选中状态，账户本身留在列表里，下次能直接切回来）。</summary>
        private void SignOut()
        {
            MainWindowViewModel.MainModel.Account = null;
            SettingsService.SetSelectedAccount(string.Empty);
            RefreshAccountCard();
            AppServices.Notifications?.Show("已登出，可随时在「账户」页切回来");
        }

        /// <summary>刷新首页的账户卡片（进设置页、切账户后都要对一次）。</summary>
        private void RefreshAccountCard()
        {
            var account = MainWindowViewModel.MainModel.Account;
            HasAccount = account is not null;

            if (account is null)
            {
                CurrentAccountName = "未登录";
                CurrentAccountTypeText = "在「账户」页登录后即可启动游戏";
                CurrentAccountAvatar = AccountPageViewModel.DefaultAvatarPath();
                return;
            }

            CurrentAccountName = string.IsNullOrWhiteSpace(account.Name) ? "未知账户" : account.Name;
            CurrentAccountTypeText = account switch
            {
                MicrosoftAccount => "微软账户",
                YggdrasilAccount => "第三方认证（Yggdrasil）",
                OfflineAccount => "离线账户",
                _ => "账户"
            };

            CurrentAccountAvatar = AccountPageViewModel.ResolveAvatarPath(account.Uuid.ToString());
        }

        /// <summary>
        /// 回填「通用 / 游戏 / 外观 / 网络 / 高级」这几组新设置的控件状态。
        ///
        /// 只写字段 + 通知，不走 setter：setter 会逐项落盘，还会在中途触发
        /// ApplyTheme / ApplyMotion 这类副作用，把还没读完的配置冲掉。
        /// </summary>
        private void FillExtendedSettings()
        {
            _suppressSettingApply = true;
            try
            {
                var s = SettingsService.Current;

                // 通用
                _autoLoginLastAccount = s.AutoLoginLastAccount;
                _minimizeToTray = s.MinimizeToTray;
                _updateIncludePrerelease = s.UpdateIncludePrerelease;
                _showHomeGreeting = s.ShowHomeGreeting;
                _showHomeLaunchCard = s.ShowHomeLaunchCard;
                _showHomeRecent = s.ShowHomeRecent;
                _showHomeInstanceOverview = s.ShowHomeInstanceOverview;
                _showHomeShortcuts = s.ShowHomeShortcuts;
                OnPropertyChanged(nameof(AutoLoginLastAccount));
                OnPropertyChanged(nameof(MinimizeToTray));
                OnPropertyChanged(nameof(UpdateIncludePrerelease));
                OnPropertyChanged(nameof(ShowHomeGreeting));
                OnPropertyChanged(nameof(ShowHomeLaunchCard));
                OnPropertyChanged(nameof(ShowHomeRecent));
                OnPropertyChanged(nameof(ShowHomeInstanceOverview));
                OnPropertyChanged(nameof(ShowHomeShortcuts));

                // 游戏
                _useManualJava = !string.IsNullOrWhiteSpace(s.JavaPath)
                                 && JavaEntries.All(j => j.JavaPath != s.JavaPath);
                OnPropertyChanged(nameof(UseManualJava));
                _manualJavaPath = s.JavaPath ?? string.Empty;
                OnPropertyChanged(nameof(ManualJavaPath));

                _maxMemoryMbValue = s.MaxMemoryMb;
                OnPropertyChanged(nameof(MaxMemoryMbValue));
                OnPropertyChanged(nameof(MaxMemorySliderText));
                _memoryAutoRecommend = s.MemoryAutoRecommend;
                OnPropertyChanged(nameof(MemoryAutoRecommend));

                _useCustomGameArgs = s.UseCustomGameArgs;
                _gameArgs = s.GameArgs ?? string.Empty;
                OnPropertyChanged(nameof(UseCustomGameArgs));
                OnPropertyChanged(nameof(GameArgs));

                _resourcePacksFolder = s.ResourcePacksFolder ?? string.Empty;
                _savesFolder = s.SavesFolder ?? string.Empty;
                OnPropertyChanged(nameof(ResourcePacksFolder));
                OnPropertyChanged(nameof(SavesFolder));
                OnPropertyChanged(nameof(ResourcePacksFolderDisplay));
                OnPropertyChanged(nameof(SavesFolderDisplay));

                _crashReportAutoUpload = s.CrashReportAutoUpload;
                OnPropertyChanged(nameof(CrashReportAutoUpload));

                // 外观
                _selectedThemeOption = ThemeOptions.FirstOrDefault(o =>
                    string.Equals(o.Key, s.Theme, StringComparison.OrdinalIgnoreCase)) ?? ThemeOptions[0];
                OnPropertyChanged(nameof(SelectedThemeOption));

                SettingsService.TryParseColor(s.AccentColor, out var accent);
                _accentR = accent.R;
                _accentG = accent.G;
                _accentB = accent.B;
                OnPropertyChanged(nameof(AccentR));
                OnPropertyChanged(nameof(AccentG));
                OnPropertyChanged(nameof(AccentB));
                OnPropertyChanged(nameof(AccentPreviewBrush));
                OnPropertyChanged(nameof(AccentHexText));

                _selectedBackgroundMode = BackgroundModeOptions.FirstOrDefault(o => o.Key == s.BackgroundMode)
                                          ?? BackgroundModeOptions[2];
                OnPropertyChanged(nameof(SelectedBackgroundMode));
                _backgroundColorHex = s.BackgroundColor;
                OnPropertyChanged(nameof(BackgroundColorHex));
                OnPropertyChanged(nameof(BackgroundColorPreviewBrush));
                _backgroundBlur = s.BackgroundBlur;
                _backgroundVignette = s.BackgroundVignette;
                _backgroundAnimated = s.BackgroundAnimated;
                OnPropertyChanged(nameof(BackgroundBlur));
                OnPropertyChanged(nameof(BackgroundVignette));
                OnPropertyChanged(nameof(BackgroundAnimated));

                _selectedAnimationLevel = AnimationLevelOptions.FirstOrDefault(o => o.Key == s.AnimationLevel)
                                          ?? AnimationLevelOptions[0];
                OnPropertyChanged(nameof(SelectedAnimationLevel));
                OnPropertyChanged(nameof(AnimationLevelHint));
                _selectedPageTransition = PageTransitionOptions.FirstOrDefault(o => o.Key == s.PageTransition)
                                          ?? PageTransitionOptions[0];
                OnPropertyChanged(nameof(SelectedPageTransition));
                _animationSpeed = s.AnimationSpeed <= 0 ? 1 : s.AnimationSpeed;
                OnPropertyChanged(nameof(AnimationSpeed));
                OnPropertyChanged(nameof(AnimationSpeedText));

                // 语言与字体
                _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Key == s.Language) ?? LanguageOptions[0];
                OnPropertyChanged(nameof(SelectedLanguage));
                _selectedFontFamily = string.IsNullOrWhiteSpace(s.FontFamily) ? "系统默认" : s.FontFamily;
                OnPropertyChanged(nameof(SelectedFontFamily));
                _fontScale = Math.Clamp(s.FontScale, SettingsService.MinFontScale, SettingsService.MaxFontScale);
                OnPropertyChanged(nameof(FontScale));
                OnPropertyChanged(nameof(FontScaleText));
                OnPropertyChanged(nameof(FontScalePercent));
                _highContrast = s.HighContrast;
                OnPropertyChanged(nameof(HighContrast));

                // 网络
                _selectedDownloadSource = DownloadSourceOptions.FirstOrDefault(o => o.Key == s.DownloadSourceMode)
                                          ?? DownloadSourceOptions[0];
                OnPropertyChanged(nameof(SelectedDownloadSource));
                _downloadSpeedLimitKb = s.DownloadSpeedLimitKb;
                OnPropertyChanged(nameof(DownloadSpeedLimitKb));
                OnPropertyChanged(nameof(DownloadSpeedLimitText));
                _downloadTimeoutSeconds = DownloadTimeoutOptions.Contains(s.DownloadTimeoutSeconds)
                    ? s.DownloadTimeoutSeconds
                    : 30;
                OnPropertyChanged(nameof(DownloadTimeoutSeconds));
            }
            finally
            {
                _suppressSettingApply = false;
            }

            RefreshAccountCard();
        }

        private SettingOption _selectedLanguage = LanguageOptions[0];
        private string _selectedFontFamily = "系统默认";
        private double _fontScale = 1.0;
        private bool _highContrast;

        public static readonly IReadOnlyList<SettingOption> LanguageOptions = new[]
        {
            new SettingOption(string.Empty, "跟随系统"),
            new SettingOption("zh-CN", "简体中文"),
            new SettingOption("en-US", "English"),
        };

        public IReadOnlyList<SettingOption> LanguageList => LanguageOptions;

        /// <summary>
        /// 界面语言。选完<b>立刻</b>生效 —— 不要求重启，翻不翻得出来是翻译层的事，
        /// 是否愿意为它重启一次不该由玩家承担。
        /// </summary>
        public SettingOption SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (value == null || !SetProperty(ref _selectedLanguage, value))
                    return;

                SettingsService.Current.Language = value.Key;
                SettingsService.Save();

                var target = value.Key switch
                {
                    "en-US" => AppLanguage.English,
                    "zh-CN" => AppLanguage.Chinese,
                    // 「跟随系统」：按系统语言重新判一次
                    _ => Localize.DetectSystemLanguage()
                };

                Localize.SetLanguage(target);
            }
        }

        /// <summary>系统字体列表（含「系统默认」）。</summary>
        public ObservableCollection<string> FontFamilyList { get; } = new() { "系统默认" };

        public string SelectedFontFamily
        {
            get => _selectedFontFamily;
            set
            {
                if (!SetProperty(ref _selectedFontFamily, value ?? "系统默认") || _suppressSettingApply)
                    return;

                SettingsService.Current.FontFamily = value == "系统默认" ? string.Empty : value;
                SettingsService.ApplyTypography();
                SettingsService.Save();
            }
        }

        /// <summary>字体缩放 80% ~ 150%。</summary>
        /// <summary>
        /// 用外部字体文件作为界面字体。
        ///
        /// <para>
        /// 字体族名必须一起带上（<c>来源#族名</c>），否则 Avalonia 只知道文件、不知道用里面哪一个字体。
        /// 族名由调用方从文件里读出来。
        /// </para>
        /// </summary>
        public void SetFontFromFile(string path, string familyName)
        {
            var source = "file:///" + path.Replace('\\', '/').TrimStart('/');
            var spec = $"{source}#{familyName}";

            if (!FontFamilyList.Contains(familyName))
                FontFamilyList.Add(familyName);

            _selectedFontFamily = familyName;
            OnPropertyChanged(nameof(SelectedFontFamily));

            SettingsService.Current.FontFamily = spec;
            SettingsService.ApplyTypography();
            SettingsService.Save();
        }

        public double FontScale
        {
            get => _fontScale;
            set
            {
                if (!SetProperty(ref _fontScale, value))
                    return;

                OnPropertyChanged(nameof(FontScaleText));
                OnPropertyChanged(nameof(FontScalePercent));

                if (_suppressSettingApply)
                    return;

                SettingsService.Current.FontScale = value;
                SettingsService.ApplyTypography();
                SettingsService.Save();
            }
        }

        public string FontScaleText => $"{_fontScale * 100:0}%";

        public double FontScalePercent
        {
            get => _fontScale * 100;
            set => FontScale = Math.Clamp(value, SettingsService.MinFontScale * 100,
                SettingsService.MaxFontScale * 100) / 100;
        }

        /// <summary>高对比度模式。</summary>
        public bool HighContrast
        {
            get => _highContrast;
            set
            {
                if (!SetProperty(ref _highContrast, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.HighContrast = value;
                SettingsService.ApplyHighContrast();
                SettingsService.Save();
            }
        }

        /// <summary>把系统字体灌进列表（首次初始化时调一次）。</summary>
        private void LoadFontFamilies()
        {
            try
            {
                foreach (var family in FontManager.Current.SystemFonts)
                {
                    var name = family.Name;
                    if (!string.IsNullOrWhiteSpace(name) && !FontFamilyList.Contains(name))
                        FontFamilyList.Add(name);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "枚举系统字体失败，字体下拉框只保留「系统默认」");
            }
        }
        // ===== 网络 / 高级 / 关于 =====

        private SettingOption _selectedDownloadSource = DownloadSourceOptions[0];
        private double _downloadSpeedLimitKb;
        private int _downloadTimeoutSeconds = 30;

        public static readonly IReadOnlyList<SettingOption> DownloadSourceOptions = new[]
        {
            new SettingOption("Auto", "自动测速（镜像优先，可回退官方）"),
            new SettingOption("Mirror", "固定镜像（只走镜像）"),
            new SettingOption("Official", "官方优先"),
        };

        public IReadOnlyList<SettingOption> DownloadSourceList => DownloadSourceOptions;

        /// <summary>下载源策略。</summary>
        public SettingOption SelectedDownloadSource
        {
            get => _selectedDownloadSource;
            set
            {
                if (value == null || !SetProperty(ref _selectedDownloadSource, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.DownloadSourceMode = value.Key;
                SettingsService.Save();
                SettingsService.ApplyDownloadSettings();

                _useBmclMirror = value.Key != "Official";
                OnPropertyChanged(nameof(UseBmclMirror));
                OnPropertyChanged(nameof(DownloadSourceSummary));
            }
        }

        /// <summary>总速度上限（KB/s），0 表示不限。</summary>
        public double DownloadSpeedLimitKb
        {
            get => _downloadSpeedLimitKb;
            set
            {
                if (!SetProperty(ref _downloadSpeedLimitKb, value))
                    return;

                OnPropertyChanged(nameof(DownloadSpeedLimitText));

                if (_suppressSettingApply)
                    return;

                SettingsService.Current.DownloadSpeedLimitKb = (int)value;
                SettingsService.Save();
            }
        }

        public string DownloadSpeedLimitText =>
            _downloadSpeedLimitKb <= 0 ? "不限" : $"{_downloadSpeedLimitKb / 1024:0.#} MB/s";

        public static readonly IReadOnlyList<int> DownloadTimeoutOptions = new[] { 10, 15, 20, 30, 45, 60, 90, 120 };
        public IReadOnlyList<int> DownloadTimeoutList => DownloadTimeoutOptions;

        /// <summary>多久收不到数据就判定连接卡死（秒）。</summary>
        public int DownloadTimeoutSeconds
        {
            get => _downloadTimeoutSeconds;
            set
            {
                if (!SetProperty(ref _downloadTimeoutSeconds, value) || _suppressSettingApply)
                    return;

                SettingsService.Current.DownloadTimeoutSeconds = value;
                SettingsService.Save();
            }
        }

        /// <summary>把日志目录打包成 zip 导出（比单个 log 文件更适合发给开发者）。</summary>
        public async Task ExportLogArchiveAsync(string destination)
        {
            try
            {
                if (File.Exists(destination))
                    File.Delete(destination);

                System.IO.Compression.ZipFile.CreateFromDirectory(
                    LogService.LogDirectory, destination,
                    System.IO.Compression.CompressionLevel.Optimal,
                    includeBaseDirectory: true);

                LogActionMessage = $"已导出日志包到 {destination}";
                AppServices.Notifications?.Success("日志包已导出");
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "导出日志压缩包失败");
                LogActionMessage = "导出失败：" + ex.Message;
                AppServices.Notifications?.Error("导出日志包失败");
            }

            await Task.CompletedTask;
        }

        /// <summary>重置配置（保留账户、游戏目录与 Java 设置）。</summary>
        public async Task ResetSettingsAsync()
        {
            var dialogs = AppServices.Dialogs;
            if (dialogs is null)
                return;

            var confirmed = await dialogs.ConfirmAsync(
                "重置设置",
                "外观、启动参数、下载与日志设置都会恢复默认值。\n" +
                "账户、游戏目录和 Java 路径会保留，存档与模组不受影响。\n\n确定继续吗？",
                "恢复默认", "取消");

            if (!confirmed)
                return;

            SettingsService.ResetToDefaults();

            // 重置后界面上所有控件要跟着回到默认值
            _isInitialized = false;
            InitializeViewModel();
            AppServices.Notifications?.Success("设置已恢复默认");
        }

        /// <summary>检查更新。</summary>
        public async Task CheckUpdateAsync()
        {
            UpdateStatusText = "正在检查…";

            var result = await UpdateService.CheckAsync(_updateIncludePrerelease);
            UpdateStatusText = result.Message;

            if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Latest is null)
            {
                AppServices.Notifications?.Show(result.Message);
                return;
            }

            var latest = result.Latest;
            var dialogs = AppServices.Dialogs;
            if (dialogs is null)
                return;

            var go = await dialogs.ConfirmAsync(
                "发现新版本",
                $"最新版本：{latest.TagName}\n发布时间：{latest.PublishedAt.ToLocalTime():yyyy-MM-dd}\n\n是否打开下载页面？",
                "打开", "稍后");

            if (go)
                OpenUrl(latest.HtmlUrl);
        }

        /// <summary>打开一个外部链接（统一的异常处理，避免点了没反应）。</summary>
        internal static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "打开链接失败：{Url}", url);
                AppServices.Notifications?.Error("打不开这个链接");
            }
        }

        /// <summary>
        /// 内嵌打开开源许可全文。
        ///
        /// 走 <see cref="AppLifetime.ShowDialogAsync(Avalonia.Controls.Control, string?)"/> 而不是弹窗：
        /// 1.0.8.0 起子页统一内嵌到主窗口内容区（学 PCL2）。构造失败也要兜住 ——
        /// 这里抛出去就是「点一下关于页就崩」，而它只是看协议。
        /// </summary>
        private void ShowLicenseWindow()
        {
            try
            {
                _ = AppLifetime.ShowDialogAsync(new Views.Windows.LicenseWindow());
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "打开开源许可页面失败");
                AppServices.Notifications?.Error("打不开许可页面");
            }
        }

        // ===== 日志 =====

        /// <summary>界面侧最多保留的日志行数，与 LogService 的内存缓冲容量对齐。</summary>
        private const int MaxVisibleLogs = 1000;

        private readonly ObservableCollection<LogEntry> _recentLogs = new();
        private LogLevelOption _selectedLogLevel = LogService.AvailableLevels[2];
        private LogLevelOption _selectedLogFilter = new("Verbose", "全部等级");
        private LogEventLevel _selectedLogFilterMinLevel = LogEventLevel.Verbose;
        private int _selectedLogRetention = 7;
        private bool _logToDebugOutput = true;
        private bool _suppressLogApply;
        private bool _hasLogs;
        private string _logSummary = string.Empty;
        private string _logActionMessage = string.Empty;

        /// <summary>可选的最低记录等级。</summary>
        public IReadOnlyList<LogLevelOption> LogLevels => LogService.AvailableLevels;

        /// <summary>查看器的等级过滤选项。</summary>
        public IReadOnlyList<LogLevelOption> LogFilters { get; } = new[]
        {
            new LogLevelOption("Verbose", "全部等级"),
            new LogLevelOption("Information", "信息及以上"),
            new LogLevelOption("Warning", "警告及以上"),
            new LogLevelOption("Error", "错误及以上"),
        };

        /// <summary>可选的日志保留天数。</summary>
        public IReadOnlyList<int> LogRetentionOptions { get; } = new[] { 3, 7, 14, 30 };

        /// <summary>日志目录。</summary>
        public string LogDirectory => LogService.LogDirectory;

        /// <summary>界面显示的日志。</summary>
        public ObservableCollection<LogEntry> RecentLogs => _recentLogs;

        public bool HasLogs
        {
            get => _hasLogs;
            private set => SetProperty(ref _hasLogs, value);
        }

        /// <summary>日志文件数量 / 占用空间 / 已显示条数。</summary>
        public string LogSummary
        {
            get => _logSummary;
            private set => SetProperty(ref _logSummary, value);
        }

        /// <summary>上一次操作（清空 / 导出）的结果提示。</summary>
        public string LogActionMessage
        {
            get => _logActionMessage;
            private set => SetProperty(ref _logActionMessage, value);
        }

        /// <summary>最低记录等级。改动即时生效，无需重启。</summary>
        public LogLevelOption SelectedLogLevel
        {
            get => _selectedLogLevel;
            set
            {
                // 先判空再 SetProperty：ComboBox 在 ItemsSource 变化时会推 null 过来
                if (value == null || !SetProperty(ref _selectedLogLevel, value) || _suppressLogApply)
                    return;

                SettingsService.Current.LogLevel = value.Key;
                LogService.ApplyLevel(value.Key);
                SettingsService.Save();

                // 立刻写一条，让用户在当前视图里就能看到等级生效（等级高于 Information 时本条不会出现）
                Serilog.Log.Information("日志等级已切换为 {Level}", value.Key);
            }
        }

        /// <summary>查看器的等级过滤。</summary>
        public LogLevelOption SelectedLogFilter
        {
            get => _selectedLogFilter;
            set
            {
                if (value == null || !SetProperty(ref _selectedLogFilter, value))
                    return;

                _selectedLogFilterMinLevel = ParseFilterLevel(value.Key);
                RefreshLogs();
            }
        }

        /// <summary>日志保留天数。改动后重建 File sink 生效。</summary>
        public int SelectedLogRetention
        {
            get => _selectedLogRetention;
            set
            {
                if (!SetProperty(ref _selectedLogRetention, value) || _suppressLogApply)
                    return;

                SettingsService.Current.LogRetentionDays = value;
                LogService.Reinitialize();
                SettingsService.Save();
                UpdateLogSummary();
                Serilog.Log.Information("日志保留天数已改为 {Days} 天", value);
            }
        }

        /// <summary>是否同时输出到调试器。</summary>
        public bool LogToDebugOutput
        {
            get => _logToDebugOutput;
            set
            {
                if (!SetProperty(ref _logToDebugOutput, value) || _suppressLogApply)
                    return;

                SettingsService.Current.LogToDebugOutput = value;
                LogService.Reinitialize();
                SettingsService.Save();
                Serilog.Log.Information("日志调试器输出：{Enabled}", value ? "开" : "关");
            }
        }

        public RelayCommand RefreshLogsCommand { get; }
        public RelayCommand ClearLogsCommand { get; }
        public RelayCommand OpenLogFolderCommand { get; }

        /// <summary>重新统计磁盘占用。</summary>
        public AsyncRelayCommand RefreshDiskUsageCommand { get; }

        /// <summary>只清掉超过保留天数的日志（当天那份永远留着）。</summary>
        public RelayCommand CleanExpiredLogsCommand { get; }

        private static LogEventLevel ParseFilterLevel(string key) =>
            Enum.TryParse<LogEventLevel>(key, ignoreCase: true, out var level)
                ? level
                : LogEventLevel.Verbose;

        // ===== 方法 =====

        private string GetAssemblyVersion()
        {
            // 与启动日志横幅共用同一份展示逻辑（形如 "1.0.4" 或 "1.0.4.1"，不含 SourceLink 的 "+<commit>"）
            return App.GetDisplayVersion();
        }

        /// <summary>
        /// 按本机内存自动挑一档最大内存：取本机内存的一半，向下就近落到已有档位。
        ///
        /// 之所以留一半给系统和其他程序 —— 把 -Xmx 顶到物理内存上限，
        /// 游戏跑起来后系统会因为换页而整体变卡。
        /// </summary>
        private void AutoConfigureMemory()
        {
            try
            {
                var totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
                if (totalMb <= 0)
                {
                    Serilog.Log.Warning("自动配置内存失败：读不到本机内存大小");
                    return;
                }

                var best = MemoryOptions.LastOrDefault(o => o.Mb <= totalMb / 2);
                if (best is null)
                {
                    Serilog.Log.Warning("自动配置内存失败：本机内存过小（{Total} MB）", totalMb);
                    return;
                }

                SelectedMaxMemory = best;

                // 滑条也要跟着走，否则点了「自动推荐」界面上还是原来的数字
                if (Math.Abs(_maxMemoryMbValue - best.Mb) > 0.5)
                {
                    _maxMemoryMbValue = best.Mb;
                    OnPropertyChanged(nameof(MaxMemoryMbValue));
                    OnPropertyChanged(nameof(MaxMemorySliderText));
                }

                Serilog.Log.Information("已按本机内存（{Total} MB）自动设为 {Mb} MB", totalMb, best.Mb);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "自动配置内存失败");
            }
        }

        private void OnChangeTheme(string? parameter)
        {
            var theme = parameter == "theme_dark" ? AppThemeMode.Dark : AppThemeMode.Light;
            if (CurrentTheme == theme)
                return;

            SettingsService.Current.Theme = theme.ToString();
            // 走统一入口：切主题的同时一并刷新背景遮罩与主题色
            SettingsService.ApplyTheme();
            CurrentTheme = theme;
            SettingsService.Save();
        }

        // ============================================================
        //  磁盘占用（高级 → 磁盘占用）
        // ============================================================

        private readonly ObservableCollection<DiskUsageEntry> _diskUsage = new();
        private bool _isDiskUsageBusy;
        private string _diskUsageSummary = string.Empty;
        private bool _diskUsageLoaded;

        /// <summary>三块目录的占用。集合本身只增删元素，不整体替换 —— 否则列表会闪一下。</summary>
        public ObservableCollection<DiskUsageEntry> DiskUsage => _diskUsage;

        public bool IsDiskUsageBusy
        {
            get => _isDiskUsageBusy;
            private set => SetProperty(ref _isDiskUsageBusy, value);
        }

        public string DiskUsageSummary
        {
            get => _diskUsageSummary;
            private set => SetProperty(ref _diskUsageSummary, value);
        }

        /// <summary>重新量一遍。统计在后台线程跑，界面上只换文案。</summary>
        public async Task RefreshDiskUsageAsync()
        {
            if (_isDiskUsageBusy)
                return;

            IsDiskUsageBusy = true;
            DiskUsageSummary = "正在统计…";

            try
            {
                var entries = await DiskUsageService.MeasureAsync();
                var total = 0L;

                _diskUsage.Clear();
                foreach (var entry in entries)
                {
                    _diskUsage.Add(entry);
                    total += entry.Bytes;
                }

                DiskUsageSummary = $"合计 {DiskUsageService.FormatSize(total)}";
                _diskUsageLoaded = true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "统计磁盘占用失败");
                DiskUsageSummary = "统计失败，可以点「重新统计」再试一次";
            }
            finally
            {
                IsDiskUsageBusy = false;
            }
        }

        /// <summary>
        /// 切到「高级」时才量一次。
        ///
        /// <para>
        /// 不在页面构造时量：设置页是 DI 单例，构造时量等于启动即枚举整个 .minecraft，
        /// 而绝大多数用户根本不会打开这个分区。
        /// </para>
        /// </summary>
        public void EnsureDiskUsageLoaded()
        {
            if (_diskUsageLoaded || _isDiskUsageBusy)
                return;

            _ = RefreshDiskUsageAsync();
        }

        private void CleanExpiredLogs()
        {
            var deleted = DiskUsageService.CleanExpiredLogs();

            AppServices.Notifications?.Success(deleted > 0
                ? $"已清理 {deleted} 个超期日志文件"
                : "没有超期日志需要清理");

            RefreshLogs();
            UpdateLogSummary(force: true);
            _ = RefreshDiskUsageAsync();
        }

        // ============================================================
        //  配置备份 / 分区重置
        // ============================================================

        private string _backupStatus = string.Empty;

        /// <summary>导入导出那一行的结果文案（成功、失败都写在这里，不弹窗）。</summary>
        public string BackupStatus
        {
            get => _backupStatus;
            private set => SetProperty(ref _backupStatus, value);
        }

        /// <summary>导出配置备份。文件路径由页面的保存对话框给出。</summary>
        public void ExportBackup(string path)
        {
            try
            {
                var count = SettingsBackupService.Export(path);
                BackupStatus = $"已导出 {count} 项设置到 {Path.GetFileName(path)}";
                AppServices.Notifications?.Success("设置备份已导出");
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "导出设置备份失败");
                BackupStatus = "导出失败：" + FriendlyError.Describe(ex);
                AppServices.Notifications?.Error("导出失败");
            }
        }

        /// <summary>
        /// 导入配置备份：先确认，再逐项合并，最后把界面整页刷回配置里的值。
        ///
        /// <para>
        /// 「整页刷回」是必须的 —— 导入改的是 <see cref="SettingsService.Current"/>，
        /// 而界面上的开关各自持有一份副本，不重新初始化就会看到「设置说开了、界面还是关的」。
        /// </para>
        /// </summary>
        public async Task ImportBackupAsync(string path)
        {
            var dialogs = AppServices.Dialogs;
            if (dialogs is null)
                return;

            var confirmed = await dialogs.ConfirmAsync(
                "导入设置备份",
                "会用文件里的设置覆盖你现在的选择，账户、游戏目录、Java 路径和语言不受影响。",
                "导入", "取消");

            if (!confirmed)
                return;

            var result = SettingsBackupService.Import(path);

            if (!result.Succeeded)
            {
                BackupStatus = result.Error ?? "导入失败";
                AppServices.Notifications?.Error("这个文件导入不了");
                return;
            }

            _isInitialized = false;
            InitializeViewModel();

            BackupStatus = result.Applied > 0
                ? $"已导入 {result.Applied} 项设置"
                : "文件里的设置和你现在的完全一致";

            AppServices.Notifications?.Success(result.Applied > 0 ? "设置已更新" : "没有需要改的项");
        }

        /// <summary>
        /// 只把某一节恢复默认。
        /// <paramref name="title"/> 是界面上那一节的名字，出现在确认框里，
        /// 让用户看清「恢复的是哪一节」而不是含糊地「恢复默认」。
        /// </summary>
        public async Task ResetSectionAsync(string section, string title)
        {
            var dialogs = AppServices.Dialogs;
            if (dialogs is null)
                return;

            // 节名先过一遍翻译：英文界面下要把 "General" 填进句子里，
            // 而不是留着「通用」两个字夹在英文句子中间
            var display = Localize.T(title);

            var confirmed = await dialogs.ConfirmAsync(
                $"恢复「{display}」的默认设置",
                $"只把「{display}」这一节的选项恢复默认，其它节和账户、游戏目录都不受影响。",
                "恢复默认", "取消");

            if (!confirmed)
                return;

            var changed = SettingsService.ResetSection(section);

            _isInitialized = false;
            InitializeViewModel();

            AppServices.Notifications?.Success(changed > 0
                ? $"「{display}」已恢复默认（改动 {changed} 项）"
                : $"「{display}」本来就是默认值");
        }
    }
}
