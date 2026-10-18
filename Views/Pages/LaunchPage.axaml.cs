using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;
using System;
using System.ComponentModel;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;
using VibrantbitLauncher.Views.Windows;

namespace VibrantbitLauncher.Views.Pages
{
    /// <summary>
    /// 启动页：左侧选版本 + 启动，右侧官方新闻图墙。
    /// 版本列表与启动逻辑复用 RunPageViewModel，新闻列表复用 DashboardPageViewModel。
    /// </summary>
    public partial class LaunchPage : UserControl
    {
        private readonly RunPageViewModel? _launchViewModel;
        private readonly DashboardPageViewModel? _newsViewModel;

        public LaunchPage()
        {
            _launchViewModel = AppServices.Provider?.GetService(typeof(RunPageViewModel)) as RunPageViewModel;
            DataContext = _launchViewModel;
            InitializeComponent();

            // 新闻区用独立的数据上下文（新闻源），避免和启动逻辑混在一起
            _newsViewModel = AppServices.Provider?.GetService(typeof(DashboardPageViewModel)) as DashboardPageViewModel;
            NewsHost.DataContext = _newsViewModel;

            // 直接赋 ItemsSource 而不是在 XAML 里绑 {Binding News}：
            // 页面的 DataContext 是 RunPageViewModel（没有 News），InitializeComponent 阶段
            // 那次求值会报一条绑定告警；这里显式给源，既准确又安静。
            NewsHost.ItemsSource = _newsViewModel?.News;

            // 版本列表是异步扫盘填进去的：页面 Loaded 那一刻集合必然还是空的，
            // 只在那时判一次「有没有版本」，就会把空状态一直挂在有版本的页面上。
            // 所以集合一变、扫描状态一变就重算一次。
            if (_launchViewModel is not null)
            {
                _launchViewModel.MinecraftVersions.CollectionChanged += OnVersionListChanged;
                _launchViewModel.PropertyChanged += OnLaunchViewModelPropertyChanged;
            }

            Loaded += async (_, _) =>
            {
                _launchViewModel?.LoadCommand.Execute(null);
                UpdateEmptyStates();

                if (_newsViewModel is { IsLoaded: false })
                    await _newsViewModel.LoadNewsAsync();
            };
        }

        private void OnVersionListChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            => UpdateEmptyStates();

        private void OnLaunchViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(RunPageViewModel.IsRefreshing) or nameof(RunPageViewModel.MinecraftVersions))
                UpdateEmptyStates();
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

        //  ==================== 新闻  ====================

        /// <summary>
        /// 单击新闻卡片：打开详情页并传入该条新闻。
        ///
        /// Avalonia 没有 WPF 那样的 MouseLeftButtonUp 元素事件，改用 PointerReleased，
        /// 并只认鼠标左键（触控 / 笔走 InitialPressMouseButton 为 None 时也放行）。
        /// </summary>
        private void OnNewsItemPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton is not (MouseButton.Left or MouseButton.None))
                return;

            if (sender is Control { DataContext: NewsItem news })
            {
                var page = AppServices.Provider?.GetService(typeof(NewsDetailPage)) as NewsDetailPage
                           ?? new NewsDetailPage();
                page.ShowNews(news);
                AppNavigation.Navigate(page);
            }
        }
    }
}
