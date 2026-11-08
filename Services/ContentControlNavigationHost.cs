using System;
using Avalonia.Controls;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 极简导航宿主：在单个 <see cref="ContentControl"/> 里切换页面实例（从 DI 解析，单例复用）。
///
/// <para>
/// 用于「不属于主界面导航体系、但页面里照样调 <c>AppNavigation.Navigate(...)</c>」的场景 ——
/// 目前是首次使用向导：它占据整个窗口（盖住侧栏与标题栏），页面之间的跳转不能落到
/// 主界面的内容宿主里，否则用户点「下一步」只会看到被盖住的那一层换页。
/// </para>
///
/// <para>
/// 只实现向导需要的能力：正向跳转 + 当前页；不支持返回栈。
/// </para>
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

        // 向导是「一步步往前走」，所以入场方向固定为从右侧落定。
        // 主窗口的页面切换由 TransitioningContentControl 负责，这里没有它，得自己播一次。
        Helpers.PageTransitionHelper.PlayEnterTransition(_host, 1);

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
