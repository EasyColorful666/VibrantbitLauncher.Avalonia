using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Windows;

namespace VibrantbitLauncher.Views.Windows;

public partial class MainWindow : FAAppWindow
{
    private readonly TransitioningContentControl _pageHost;
    private NavigationService? _navigation;

    public MainWindow()
    {
        InitializeComponent();

        TitleBar.ExtendsContentIntoTitleBar = true;

        // 页面宿主：FANavigationView.Content 固定是这个过渡控件，
        // 之后所有页面切换都写进它的 Content，由 PageTransition 负责演出。
        // 放在导航视图内部（而不是包在外面）是为了让 FANavigationView 自己
        // 负责的内容边距、快捷键等在过渡期间仍然生效。
        _pageHost = new TransitioningContentControl
        {
            PageTransition = new FluidPageTransition(),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };

        RootNavigation.Content = _pageHost;

        // ItemInvoked 是 CLR 事件（TypedEventHandler），XAML 无法直接挂，必须代码里订阅
        RootNavigation.ItemInvoked += OnNavigationItemInvoked;

        var vm = new MainWindowViewModel();
        DataContext = vm;

        // 把导航服务挂到全局入口，供各 ViewModel 直接跳转
        _navigation = new NavigationService(_pageHost);
        _navigation.Navigated += OnNavigated;
        vm.Navigation = _navigation;
        AppNavigation.Current = _navigation;

        Application.Current!.ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    /// <summary>轻提示宿主，供 App 启动时装配 NotificationService。</summary>
    public Panel NotificationHost => SnackbarHost;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 先播窗口入场，再切页面：入场是「外壳浮出来」，页面切换是「内容浮上来」，
        // 两者叠在一起会互相抢戏，所以入场只给外壳（含侧边栏），页面走自己的过渡。
        PageTransitionHelper.PlayWindowEntrance(AppRoot);

        // 默认进入「启动」页
        _navigation?.NavigateFromMenu(ResolvePage("LaunchPage"));

        if (IsWindows11 && ActualThemeVariant != FluentAvaloniaTheme.HighContrastTheme)
            TryEnableMicaEffect();
    }

    /// <summary>
    /// 导航发生后同步侧边栏的选中态。
    ///
    /// FANavigationView 只在**用户点击菜单项**时自己更新 SelectedItem；
    /// 代码里跳转（ViewModel 发 NavigateTo、返回栈 GoBack 等）只换了 Content，
    /// SelectedItem 仍停在上一个菜单项 —— 表现就是「页面切了，但左侧栏不高亮对应的项」。
    /// </summary>
    private void OnNavigated(object? sender, object? page)
    {
        if (page is null)
            return;

        SyncPaneSelection(page.GetType().Name);
    }

    /// <summary>
    /// 按页面类型名（与菜单项 Tag 同名）选中对应的侧边栏项。
    /// 没有对应菜单项的页面（版本管理 / 安装 / 资源详情 / 新闻详情等）保持当前选中，
    /// 这样从「启动」进「版本管理」时侧边栏仍高亮「启动」，符合层级直觉。
    /// </summary>
    private void SyncPaneSelection(string pageName)
    {
        var item = FindMenuItem(RootNavigation.MenuItems, pageName)
                   ?? FindMenuItem(RootNavigation.FooterMenuItems, pageName);
        if (item is null)
            return;

        if (!ReferenceEquals(RootNavigation.SelectedItem, item))
            RootNavigation.SelectedItem = item;
    }

    private static FANavigationViewItem? FindMenuItem(System.Collections.IEnumerable? items, string tag)
    {
        if (items is null)
            return null;

        foreach (var entry in items)
        {
            if (entry is FANavigationViewItem navItem
                && string.Equals(navItem.Tag?.ToString(), tag, StringComparison.Ordinal))
                return navItem;
        }

        return null;
    }

    /// <summary>导航项点击：按 Tag 解析页面并切换（同时同步选中态）。</summary>
    private void OnNavigationItemInvoked(object? sender, FANavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not FANavigationViewItem item)
            return;

        var tag = item.Tag?.ToString();
        if (string.IsNullOrEmpty(tag))
            return;

        var page = ResolvePage(tag);
        if (page is not null)
            _navigation?.NavigateFromMenu(page);
    }

    private static object? ResolvePage(string pageName)
    {
        var type = typeof(MainWindow).Assembly
            .GetType($"VibrantbitLauncher.Views.Pages.{pageName}");
        if (type is null)
        {
            Serilog.Log.Warning("找不到页面类型：{Page}", pageName);
            return null;
        }

        return AppServices.Provider?.GetService(type) ?? Activator.CreateInstance(type);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (!IsWindows11)
            return;

        if (ActualThemeVariant != FluentAvaloniaTheme.HighContrastTheme)
            TryEnableMicaEffect();
        else
        {
            ClearValue(BackgroundProperty);
            ClearValue(TransparencyBackgroundFallbackProperty);
        }
    }

    /// <summary>开启 Mica 云母材质：窗口底色透明 + 系统材质透出。</summary>
    private void TryEnableMicaEffect()
    {
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica };
    }
}
