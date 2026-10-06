namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 全局导航入口。
    ///
    /// 页面 ViewModel 里常有「点一下跳到另一个页面」的需求（下载 → 安装、
    /// 下载任务 → 下载中心）。若给每个 ViewModel 都注入 INavigationService，
    /// 构造函数签名会被污染，而且早期初始化的 ViewModel 拿不到窗口。
    /// 这里用一个静态宿主保存主窗口实现的导航服务，供各 ViewModel 直接取用。
    /// </summary>
    public static class AppNavigation
    {
        /// <summary>当前生效的导航服务；主窗口构造时注入。</summary>
        public static INavigationService? Current { get; set; }

        /// <summary>跳转到指定页面类型。无宿主时静默忽略。</summary>
        public static bool Navigate(Type pageType) => Current?.Navigate(pageType) ?? false;

        /// <summary>跳转到指定页面实例。无宿主时静默忽略。</summary>
        public static bool Navigate(object page) => Current?.Navigate(page) ?? false;
    }
}
