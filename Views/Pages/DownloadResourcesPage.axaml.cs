using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

/// <summary>
/// 资源搜索页（模组 / 整合包 / 数据包 / 资源包 / 光影共用）。
/// </summary>
public partial class DownloadResourcesPage : UserControl
{
    /// <summary>距底部多少像素时触发加载下一页。</summary>
    private const double LoadMoreThreshold = 320;

    private ScrollViewer? _listScrollViewer;
    private bool _loadMoreRequested;
    private DownloadResourcesViewModel? _viewModel;

    public DownloadResourcesPage()
    {
        // 每次都由页面自己 new 一个 ViewModel：下载中心里模组/整合包/数据包/资源包/光影
        // 会同时各建一个本页面实例，若共用 DI 单例 ViewModel，五个分类的筛选条件、
        // 分页游标、搜索结果会互相串数据（切到光影却看到模组的列表）。
        _viewModel = new DownloadResourcesViewModel();

        DataContext = _viewModel;
        InitializeComponent();

        Loaded += OnLoaded;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 程序运行后 1.3 秒的启动刷新（主窗口广播）。页面若还没被创建就收不到 ——
        // 那种情况下由「切入页面」那次刷新兜底，见 DownloadHubPage。
        WeakReferenceMessenger.Default.Register<RefreshDownloadDataMessage>(
            this, (_, _) => RefreshData());
    }

    /// <summary>
    /// 重新拉取「Minecraft 版本列表 + 本分类的资源列表」。
    /// 三个触发点：启动后 1.3 秒的广播、切入下载中心、以及在本页内部切回这个分类。
    /// </summary>
    public void RefreshData() => _ = _viewModel?.RefreshAllAsync();

    /// <summary>由下载中心在切换分类时调用，指定这个页面展示哪一类资源。</summary>
    public void SetProjectType(string projectType) => _viewModel?.SetProjectType(projectType);

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 虚拟化列表的 ScrollViewer 要等容器生成后才在视觉树里，取一次缓存下来
        _listScrollViewer = listBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_listScrollViewer != null)
        {
            _listScrollViewer.ScrollChanged -= OnListScrollChanged;
            _listScrollViewer.ScrollChanged += OnListScrollChanged;
        }

        UpdateStates();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadResourcesViewModel.IsLoading)
            or nameof(DownloadResourcesViewModel.IsLoadingMore)
            or nameof(DownloadResourcesViewModel.TotalHits)
            or nameof(DownloadResourcesViewModel.HasError)
            or nameof(DownloadResourcesViewModel.ErrorText))
        {
            UpdateStates();
        }
    }

    private void UpdateStates()
    {
        if (_viewModel is null)
            return;

        loadingText.IsVisible = _viewModel.IsLoading;
        loadMorePanel.IsVisible = _viewModel.IsLoadingMore;

        // 只有在「没在加载、且一条都没有」时才显示空状态块。
        // 有内容时即便上一次请求失败也不遮挡列表（失败信息已在别处提示）。
        var showEmpty = _viewModel.ModrinthMods.Count == 0 && !_viewModel.IsLoading;
        emptyPanel.IsVisible = showEmpty;

        if (!showEmpty)
            return;

        // 区分「加载失败」与「真的没有匹配结果」——
        // 前者要给原因 + 重试按钮，后者才是提示换筛选条件。
        var failed = _viewModel.HasError;
        emptyIcon.Symbol = failed
            ? FluentAvalonia.UI.Controls.FASymbol.CloudOffline
            : FluentAvalonia.UI.Controls.FASymbol.ZoomInFilled;
        emptyText.Text = failed
            ? _viewModel.ErrorText
            : "没有匹配的资源，换个关键词或筛选条件试试";
        emptyRetryButton.IsVisible = failed;
    }

    /// <summary>
    /// 滚动接近底部时追加下一页。
    /// 用「快到底部」提前量而不是「正好到底」：等滚到最底部才请求的话，
    /// 用户会看到列表停住等网络，观感上像卡住了。
    /// </summary>
    private void OnListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || _viewModel is null)
            return;

        var remaining = viewer.Extent.Height - viewer.Viewport.Height - viewer.Offset.Y;
        if (remaining > LoadMoreThreshold)
        {
            _loadMoreRequested = false;
            return;
        }

        // 一次触底只请求一页，避免惯性滚动时连发
        if (_loadMoreRequested)
            return;

        if (!_viewModel.CanLoadMore || _viewModel.IsLoading || _viewModel.IsLoadingMore)
            return;

        _loadMoreRequested = true;
        _viewModel.LoadMoreCommand.Execute(null);
    }

    /// <summary>
    /// 卡片整行可双击：双击打开项目详情。
    /// 单击不触发任何动作，避免「选中的时候误开详情」。
    /// </summary>
    private async void OnCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ModrinthMod mod })
            await OpenProjectAsync(mod);
    }

    private async Task OpenProjectAsync(ModrinthMod mod)
    {
        if (string.IsNullOrWhiteSpace(mod.ProjectId))
        {
            AppServices.Notifications?.Warning($"资源「{mod.Name}」没有有效的项目 ID");
            return;
        }

        var navigated = AppNavigation.Navigate(typeof(ModPage));
        if (!navigated)
        {
            AppServices.Notifications?.Error("导航到资源详情页失败");
            return;
        }

        // 把当前分类（模组 / 资源包 / 光影 …）一并带过去：详情页据此决定「添加到实例」落到哪个子目录，
        // 以及整合包 / 数据包这类不能直接拷进实例的类型要不要给出该入口。
        if (AppServices.Provider?.GetService(typeof(ModPage)) is ModPage modPage)
            _ = modPage.LoadForProject(mod.ProjectId, _viewModel?.ProjectType);
    }
}
