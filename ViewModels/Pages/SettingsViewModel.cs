using CommunityToolkit.Mvvm.ComponentModel;
using VibrantbitLauncher.Helpers;
using CommunityToolkit.Mvvm.Input;
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
using Avalonia.Threading;
using Serilog.Events;
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

        public ICommand ChangeThemeCommand { get; private set; }
        public ICommand RefreshJavaCommand { get; private set; }

        /// <summary>打开当前的 .minecraft 目录（概览面板的快捷入口）。</summary>
        public RelayCommand OpenGameFolderCommand { get; }

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

            RefreshLogsCommand = new RelayCommand(RefreshLogs);
            ClearLogsCommand = new RelayCommand(ClearLogs);
            OpenLogFolderCommand = new RelayCommand(LogService.OpenLogDirectory);

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

            // 每次重新进入：刷新日志 + 重新检测 Java
            RefreshLogs();
            await LoadJavaAsync();
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
                // 用自带实现而非 JavaUtil.EnumerableJavaAsync()：后者遇到空路径候选会整体抛异常
                var javas = await JavaHelper.FindJavasAsync();

                JavaEntries.Clear();
                foreach (var j in javas)
                    JavaEntries.Add(j);

                Serilog.Log.Information("Java 查找：{Count} 个 → {List}", javas.Count, string.Join(" | ", javas.Select(j => $"{j.JavaVersion} ({j.JavaPath})")));

                var saved = MainWindowViewModel.MainModel.JavaPath;
                SelectedJava = !string.IsNullOrEmpty(saved)
                    ? JavaEntries.FirstOrDefault(j => j.JavaPath == saved)
                    : JavaEntries.FirstOrDefault();
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
            }
        }

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
    }
}
