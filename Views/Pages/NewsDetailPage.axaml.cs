using Avalonia.Controls;
using Avalonia.Interactivity;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Pages;

/// <summary>
/// 官方新闻详情（主窗口内的页面，不是独立窗口）。
/// 数据由调用方通过 ShowNews 直接传入，避免依赖消息的到达时序。
/// </summary>
public partial class NewsDetailPage : UserControl
{
    public NewsDetailPage()
    {
        InitializeComponent();
    }

    public void ShowNews(NewsItem news) => DataContext = news;

    private void OnBackClick(object? sender, RoutedEventArgs e)
        => AppNavigation.Navigate(typeof(LaunchPage));
}
