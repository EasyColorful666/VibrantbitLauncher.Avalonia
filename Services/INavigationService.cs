using Avalonia.Controls;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 导航服务：替代 WPF-UI 的 INavigationService / INavigationWindow。
    /// 由主窗口实现，页面通过它请求跳转。
    /// </summary>
    public interface INavigationService
    {
        /// <summary>当前显示的页面。</summary>
        object? CurrentPage { get; }

        /// <summary>导航到指定页面类型（从 DI 解析实例）。</summary>
        bool Navigate(Type pageType);

        /// <summary>导航到指定页面实例。</summary>
        bool Navigate(object page);

        /// <summary>返回上一页。</summary>
        bool GoBack();

        /// <summary>是否可返回。</summary>
        bool CanGoBack { get; }

        /// <summary>导航栏选中项同步事件。</summary>
        event EventHandler<object?>? Navigated;
    }
}
