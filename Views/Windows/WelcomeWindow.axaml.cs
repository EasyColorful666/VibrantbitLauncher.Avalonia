using System;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.Views.Pages;

namespace VibrantbitLauncher.Views.Windows;

/// <summary>
/// 首次启动的开机向导宿主窗口。
///
/// 向导在独立窗口里跑，页面却统一用 <c>AppNavigation.Navigate(...)</c> 请求跳转
/// （与服务主窗口页面同一套写法）。所以窗口构造时把全局导航入口临时切到本窗口的
/// 宿主，关闭时再还原给主窗口 —— 向导页面无需关心自己在哪个窗口里。
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly INavigationService? _previousNavigation;
    private readonly ContentControlNavigationHost _host;

    public WelcomeWindow()
    {
        InitializeComponent();

        _host = new ContentControlNavigationHost(PageHost);

        // 接管全局导航，让向导页面里的 AppNavigation.Navigate 落到本窗口
        _previousNavigation = AppNavigation.Current;
        AppNavigation.Current = _host;

        Closed += (_, _) =>
        {
            // 先摘掉页面再还原导航入口：向导页面是 DI 单例，若仍挂在 PageHost 上，
            // 下次再打开向导时同一实例会被二次挂载，报 "already has a visual parent"。
            PageHost.Content = null;
            AppNavigation.Current = _previousNavigation;
        };

        _host.Navigate(typeof(WelcomePage));
    }
}

/// <summary>
/// 极简导航宿主：在单个 <see cref="ContentControl"/> 里切换页面实例（从 DI 解析，单例复用）。
/// 只实现向导需要的能力：正向跳转 + 当前页；不支持返回栈。
/// </summary>
public sealed class ContentControlNavigationHost : INavigationService
{
    private readonly ContentControl _host;

    public ContentControlNavigationHost(ContentControl host) => _host = host;

    public object? CurrentPage => _host.Content;

    public bool CanGoBack => false;

    public event EventHandler<object?>? Navigated;

    public bool Navigate(Type pageType) => Navigate(ResolvePage(pageType));

    public bool Navigate(object page)
    {
        if (page is null)
            return false;

        _host.Content = page;

        if (page is INavigationAware aware)
            _ = aware.OnNavigatedToAsync();

        Navigated?.Invoke(this, page);
        return true;
    }

    public bool GoBack() => false;

    private static object? ResolvePage(Type pageType)
    {
        var provider = AppServices.Provider;
        var instance = provider?.GetService(pageType);
        if (instance is not null)
            return instance;

        try
        {
            return Activator.CreateInstance(pageType);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "无法创建向导页面实例：{Page}", pageType.FullName);
            return null;
        }
    }
}
