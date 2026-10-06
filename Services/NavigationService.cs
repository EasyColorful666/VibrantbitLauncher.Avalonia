using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 导航服务实现。
    ///
    /// 页面不再直接塞进 FANavigationView.Content，而是写进主窗口准备的内容宿主
    /// （一个 TransitioningContentControl）：由宿主负责播放切换过渡，
    /// 导航服务只管「换哪个页面 / 压不压栈」，两件事解耦。
    ///
    /// 维护一个页面栈以支持返回；页面实例优先从 DI 容器解析（保证单例复用），
    /// 解析不到时回退到无参构造。
    /// </summary>
    public sealed class NavigationService : INavigationService
    {
        private readonly ContentControl _contentHost;
        private readonly Stack<object> _backStack = new();

        public NavigationService(ContentControl contentHost)
        {
            _contentHost = contentHost;
        }

        public object? CurrentPage => _contentHost.Content;

        public bool CanGoBack => _backStack.Count > 0;

        public event EventHandler<object?>? Navigated;

        public bool Navigate(Type pageType)
        {
            var page = ResolvePage(pageType);
            return page is not null && Navigate(page);
        }

        public bool Navigate(object page)
        {
            if (page is null)
                return false;

            var previous = _contentHost.Content;
            if (previous is not null)
                _backStack.Push(previous);

            ApplyNavigation(page);
            return true;
        }

        /// <summary>
        /// 由导航项点击触发的切换：不压栈（视作顶层导航），并复位返回栈。
        /// </summary>
        public bool NavigateFromMenu(object page)
        {
            if (page is null)
                return false;

            _backStack.Clear();
            ApplyNavigation(page);
            return true;
        }

        public bool GoBack()
        {
            if (_backStack.Count == 0)
                return false;

            var page = _backStack.Pop();
            ApplyNavigation(page, pushHistory: false);
            return true;
        }

        private void ApplyNavigation(object page, bool pushHistory = true)
        {
            void Apply()
            {
                // 赋值即触发宿主的过渡动画；页面从 DI 解析、实例复用，
                // 所以这里换的是同一个对象引用（重复导航到当前页时宿主会自行跳过动画）
                _contentHost.Content = page;

                // 通知页面自身（INavigationAware）
                if (page is INavigationAware aware)
                    _ = aware.OnNavigatedToAsync();

                Navigated?.Invoke(this, page);
            }

            if (Dispatcher.UIThread.CheckAccess())
                Apply();
            else
                Dispatcher.UIThread.Post(Apply);
        }

        private static object? ResolvePage(Type pageType)
        {
            // 优先走 DI 容器，保证单例页面被复用
            var provider = AppServices.Provider;
            if (provider is not null)
            {
                var instance = provider.GetService(pageType);
                if (instance is not null)
                    return instance;
            }

            try
            {
                return Activator.CreateInstance(pageType);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "无法创建页面实例：{Page}", pageType.FullName);
                return null;
            }
        }
    }
}
