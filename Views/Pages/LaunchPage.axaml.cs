using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;
using MinecraftLaunch.Base.Models.Game;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;
using VibrantbitLauncher.ViewModels.Windows;
using VibrantbitLauncher.Views.Windows;

namespace VibrantbitLauncher.Views.Pages
{
    /// <summary>
    /// 启动页：左侧选版本 + 启动，右侧快捷设置（游戏目录 / Java / 主题）。
    /// 版本列表与启动逻辑复用 RunPageViewModel。
    /// </summary>
    public partial class LaunchPage : UserControl
    {
        private readonly RunPageViewModel? _launchViewModel;

        /// <summary>快捷设置里 Java 下拉框的数据源。</summary>
        private readonly ObservableCollection<JavaEntry> _quickJavas = new();

        /// <summary>正在把设置回填到控件：此时控件事件不应反写配置。</summary>
        private bool _quickRefreshing;

        public LaunchPage()
        {
            _launchViewModel = AppServices.Provider?.GetService(typeof(RunPageViewModel)) as RunPageViewModel;
            DataContext = _launchViewModel;
            InitializeComponent();

            quickJavaCombo.ItemsSource = _quickJavas;

            // Avalonia 的 ToggleButton（RadioButton 的基类）没有 XAML 的 Checked/Unchecked
            // 事件属性，只能在代码里订阅 IsCheckedChanged。
            quickThemeLight.IsCheckedChanged += (_, _) => OnQuickThemeChanged(quickThemeLight);
            quickThemeDark.IsCheckedChanged += (_, _) => OnQuickThemeChanged(quickThemeDark);

            // 版本列表是异步扫盘填进去的：页面 Loaded 那一刻集合必然还是空的，
            // 只在那时判一次「有没有版本」，就会把空状态一直挂在有版本的页面上。
            // 所以集合一变、扫描状态一变就重算一次。
            if (_launchViewModel is not null)
            {
                _launchViewModel.MinecraftVersions.CollectionChanged += OnVersionListChanged;
                _launchViewModel.PropertyChanged += OnLaunchViewModelPropertyChanged;
            }

            Loaded += (_, _) =>
            {
                _launchViewModel?.LoadCommand.Execute(null);
                UpdateEmptyStates();
                RefreshQuickSettings();
            };
        }

        private void OnVersionListChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            => UpdateEmptyStates();

        private void OnLaunchViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(RunPageViewModel.IsRefreshing) or nameof(RunPageViewModel.MinecraftVersions))
            {
                UpdateEmptyStates();
            }
            else if (e.PropertyName is nameof(RunPageViewModel.DetectedJavas))
            {
                // Java 扫描是后台完成的：扫完要把结果补进快捷设置的下拉框
                RefreshQuickSettings();
            }
        }

        /// <summary>
        /// 只在**真的一个版本都没有**时显示空状态；有版本就整块隐藏。
        ///
        /// <para>
        /// 扫盘还没结束时不清空为「还没有可用的游戏版本」，而是显示「正在扫描…」——
        /// 否则首次进入（磁盘慢、版本多）会先闪一下「没有版本」，看起来像 bug。
        /// 底部工具栏上的「配装码」按钮不受这里影响，永远可见。
        /// </para>
        /// </summary>
        private void UpdateEmptyStates()
        {
            if (_launchViewModel is null)
            {
                versionEmptyState.IsVisible = true;
                return;
            }

            var count = _launchViewModel.MinecraftVersions.Count;
            versionEmptyState.IsVisible = count == 0;

            if (count > 0)
                return;

            var scanning = _launchViewModel.IsRefreshing;
            emptyIcon.IsVisible = !scanning;
            emptyTitle.Text = scanning ? "正在扫描本地版本…" : "还没有可用的游戏版本";
            emptyHint.IsVisible = !scanning;
            emptyLoadoutButton.IsVisible = !scanning;
        }

        private void OnLaunchClick(object sender, RoutedEventArgs e)
        {
            if (listBox.SelectedItem is LocalVersion version)
                version.RunCommand?.Execute(version.Version);
            else
                ShowHint("请先选择一个版本");
        }

        private void OnVersionSettingsClick(object sender, RoutedEventArgs e)
        {
            if (listBox.SelectedItem is LocalVersion version)
            {
                // 先导航再发版本号：版本管理页是懒加载单例，页面没创建时消息没人接收
                // （详见 RunPageViewModel.Settings 的注释）
                WeakReferenceMessenger.Default.Send<Type, string>(typeof(VersionManagePage), "NavigateTo");
                WeakReferenceMessenger.Default.Send<string, string>(version.Version, "McVersionForManage");
            }
            else
            {
                ShowHint("请先选择一个版本");
            }
        }

        private void ShowHint(string message)
            => AppServices.Notifications?.Warning(message, "提示");

        /// <summary>
        /// 「配装码」：粘贴一串分享码，从零装出一个完整实例。
        ///
        /// <para>
        /// 之所以单独给个入口而不是让用户去「工具箱 → 配装分享」：
        /// 那条路径要先有实例才能进（窗口顶部是实例下拉框），而**刚拿到新电脑、一个版本都没装**
        /// 恰恰是最需要「一键装好」的时候。所以这个入口不依赖任何已有实例。
        /// </para>
        /// </summary>
        private async void OnLoadoutCodeClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                var window = new LoadoutImportWindow();
                await AppLifetime.ShowDialogAsync(window);

                // 只有真装出了实例才刷新列表 —— 打开看一眼就关掉时不该无谓地重扫磁盘
                if (window.Installed)
                    _launchViewModel?.LoadCommand.Execute(null);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "打开配装码窗口失败");
                AppServices.Notifications?.Error("无法打开配装码窗口：" + FriendlyError.Describe(ex));
            }
        }

        //  ==================== 快捷设置  ====================

        /// <summary>
        /// 把当前配置反映到右侧面板：游戏目录、Java（下拉 + 当前值）、亮暗主题。
        ///
        /// <para>
        /// 回填期间用 <see cref="_quickRefreshing"/> 掐断控件事件 ——
        /// 否则「设置 RadioButton.IsChecked」会走 Checked 事件再写一遍配置，
        /// 主题切换甚至可能来回触发。
        /// </para>
        /// </summary>
        private void RefreshQuickSettings()
        {
            _quickRefreshing = true;
            try
            {
                quickMcFolderText.Text = SettingsService.ResolveMinecraftFolder();

                _quickJavas.Clear();

                var detected = _launchViewModel?.DetectedJavas;
                if (detected is not null)
                {
                    foreach (var java in detected)
                        _quickJavas.Add(java);
                }

                // 用户手动指定的 Java 也要在下拉里出现，否则「当前值」对不上任何一项，
                // ComboBox 会显示空白，看起来像没设置。
                // 正常情况下 RunPageViewModel 已经把它探测过并放进了列表（带版本号），
                // 这里只是最后一道兜底 —— 版本号空着会给个可读占位，别留一行空白。
                var customJava = MainWindowViewModel.MainModel.JavaPath;
                if (!string.IsNullOrWhiteSpace(customJava)
                    && _quickJavas.All(j => !string.Equals(j.JavaPath, customJava, StringComparison.OrdinalIgnoreCase)))
                {
                    _quickJavas.Add(new JavaEntry
                    {
                        JavaPath = customJava,
                        JavaVersion = "（手动指定）",
                        Is64bit = true,
                    });
                }

                quickJavaCombo.SelectedItem = string.IsNullOrWhiteSpace(customJava)
                    ? null
                    : _quickJavas.FirstOrDefault(j =>
                        string.Equals(j.JavaPath, customJava, StringComparison.OrdinalIgnoreCase));

                quickJavaText.Text = string.IsNullOrWhiteSpace(customJava)
                    ? "未设置，启动时自动选择"
                    : customJava;

                var dark = string.Equals(SettingsService.Current.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
                quickThemeDark.IsChecked = dark;
                quickThemeLight.IsChecked = !dark;
            }
            finally
            {
                _quickRefreshing = false;
            }
        }

        /// <summary>改游戏目录：写完立刻重扫版本列表，左侧马上反映新目录里的实例。</summary>
        private async void OnQuickMcFolderClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                var picker = AppServices.FilePicker;
                if (picker is null)
                    return;

                var folder = await picker.PickFolderAsync("选择 Minecraft 文件夹");
                if (string.IsNullOrWhiteSpace(folder))
                    return;

                MainWindowViewModel.MainModel.MinecraftFolder = folder;
                SettingsService.Current.MinecraftFolder = folder;
                SettingsService.Save();

                RefreshQuickSettings();
                _launchViewModel?.LoadCommand.Execute(null);

                AppServices.Notifications?.Show(
                    "已切换到新的 Minecraft 文件夹", "快捷设置", NotificationSeverity.Success);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "切换 Minecraft 文件夹失败");
                AppServices.Notifications?.Error("切换文件夹失败：" + FriendlyError.Describe(ex));
            }
        }

        /// <summary>下拉里选一个已检测到的 Java。</summary>
        private void OnQuickJavaSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_quickRefreshing)
                return;

            if (quickJavaCombo.SelectedItem is JavaEntry java && !string.IsNullOrWhiteSpace(java.JavaPath))
                ApplyQuickJava(java.JavaPath);
        }

        /// <summary>手动挑一个 java.exe（可以是设置里没扫到的路径）。</summary>
        private async void OnQuickJavaBrowseClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                var picker = AppServices.FilePicker;
                if (picker is null)
                    return;

                var path = await picker.PickFileAsync("选择 java.exe", "exe");
                if (string.IsNullOrWhiteSpace(path))
                    return;

                if (!JavaHelper.IsUsableJava(path))
                {
                    AppServices.Notifications?.Warning(
                        "这个文件不是可用的 Java（同目录下应当有 java.dll）", "快捷设置");
                    return;
                }

                ApplyQuickJava(path);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "选择 Java 失败");
                AppServices.Notifications?.Error("选择 Java 失败：" + FriendlyError.Describe(ex));
            }
        }

        private void ApplyQuickJava(string path)
        {
            MainWindowViewModel.MainModel.JavaPath = path;
            SettingsService.Current.JavaPath = path;
            SettingsService.Save();

            RefreshQuickSettings();

            AppServices.Notifications?.Show($"已切换 Java：{path}", "快捷设置", NotificationSeverity.Success);
        }

        /// <summary>亮色 / 暗色切换，立即生效（不用重启）。</summary>
        private void OnQuickThemeChanged(RadioButton source)
        {
            if (_quickRefreshing || source.IsChecked != true)
                return;

            var theme = ReferenceEquals(source, quickThemeDark) ? "Dark" : "Light";
            if (string.Equals(SettingsService.Current.Theme, theme, StringComparison.OrdinalIgnoreCase))
                return;

            SettingsService.Current.Theme = theme;
            // 走统一入口：切主题时一并刷新窗口底色、背景遮罩与主题色
            SettingsService.ApplyTheme();
            SettingsService.Save();
        }
    }
}
