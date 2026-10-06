using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Messaging;
using System;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

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

            Loaded += async (_, _) =>
            {
                _launchViewModel?.LoadCommand.Execute(null);
                UpdateEmptyStates();

                if (_newsViewModel is { IsLoaded: false })
                    await _newsViewModel.LoadNewsAsync();
            };
        }

        /// <summary>根据版本列表实际条数切换空状态提示的显隐。</summary>
        private void UpdateEmptyStates()
            => versionEmptyState.IsVisible = (_launchViewModel?.MinecraftVersions.Count ?? 0) == 0;

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
                WeakReferenceMessenger.Default.Send<string, string>(version.Version, "McVersionForManage");
                WeakReferenceMessenger.Default.Send<Type, string>(typeof(VersionManagePage), "NavigateTo");
            }
            else
            {
                ShowHint("请先选择一个版本");
            }
        }

        private void ShowHint(string message)
            => AppServices.Notifications?.Warning(message, "提示");

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
