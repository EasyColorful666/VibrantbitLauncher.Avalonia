namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 页面生命周期感知：替代 Wpf.Ui.Abstractions.Controls.INavigationAware。
    /// 页面被导航进入 / 离开时由导航服务回调。
    /// </summary>
    public interface INavigationAware
    {
        /// <summary>导航进入该页面时调用。</summary>
        Task OnNavigatedToAsync();

        /// <summary>导航离开该页面时调用。</summary>
        Task OnNavigatedFromAsync();
    }
}
