using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
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

        ApplyPerformanceModeVisuals();

        SetUpDragDrop();

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

        // 导航项在控件模板里把 RenderTransform / Transitions 写死了，应用级样式压不过它，
        // 点击反馈只能靠代码挂（详见 InteractionFeedback 的注释）
        AttachMenuItemFeedback();

        var vm = new MainWindowViewModel();
        DataContext = vm;

        // 把导航服务挂到全局入口，供各 ViewModel 直接跳转
        _navigation = new NavigationService(_pageHost);
        _navigation.Navigated += OnNavigated;
        vm.Navigation = _navigation;
        AppNavigation.Current = _navigation;

        Application.Current!.ActualThemeVariantChanged += OnActualThemeVariantChanged;

        // Mica 能否真正渲染由 DWM 决定（系统关「透明效果」/ 远程桌面 / 虚拟机都会让它回退）。
        // 实际达成的透明级别一旦变化（含窗口显示后首次确定），就按它校正窗口底色，
        // 保证「请求了 Mica 但系统没给」的机器不会整窗穿透到桌面。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty)
                UpdateMicaBackground();
        };
    }

    /// <summary>轻提示宿主，供 App 启动时装配 NotificationService。</summary>
    public Panel NotificationHost => SnackbarHost;

    /// <summary>
    /// 性能模式下的视觉降级。
    ///
    /// 1) 给窗口根节点挂 <c>performanceMode</c> 类 → Styles/Animations.axaml 里
    ///    对应的一组样式把过渡 / 阴影 / 缩放整体关掉（见那里的注释）；
    /// 2) 位图缩放降到 LowQuality：HighQuality 走的是高质量重采样，
    ///    每次尺寸变化都要重算，低配机器上缩放窗口时会明显卡；
    /// 3) 壁纸不加载（后台 VM 会在 <c>ApplyBackground</c> 时读到这个标志并直接返回 null）。
    /// </summary>
    private void ApplyPerformanceModeVisuals()
    {
        if (!SettingsService.Current.PerformanceMode)
            return;

        Classes.Add("performanceMode");

        // 标题栏图标 + 壁纸都改走低开销插值
        RenderOptions.SetBitmapInterpolationMode(BackgroundImageLayer, BitmapInterpolationMode.LowQuality);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 内嵌子页宿主要有窗口可依附
        Instance = this;

        var perfMode = SettingsService.Current.PerformanceMode;

        // 性能模式：直接跳过窗口入场动画。
        // AppRoot 的初始透明度是 0（由动画负责推上去），低配机器上动画一卡就长时间停在
        // 半透明状态，观感就是「白屏 / 灰屏」。这里显式摆到终态，保证立刻可见。
        if (perfMode)
        {
            // AnimationHelper 会从 Opacity=0 起播这些变换；低配机器上一卡就长时间停在
            // 「淡 + 缩」的半路，观感就是糊了一层。这里直接摆到终态，保证立刻可见。
            AppRoot.Opacity = 1;

            var (scale, translate) = EnsureAppRootTransform();
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            translate.Y = 0;
        }
        else
        {
            // 先播窗口入场，再切页面：入场是「外壳浮出来」，页面切换是「内容浮上来」，
            // 两者叠在一起会互相抢戏，所以入场只给外壳（含侧边栏），页面走自己的过渡。
            PageTransitionHelper.PlayWindowEntrance(AppRoot);
        }

        // 默认进入「启动」页
        _navigation?.NavigateFromMenu(ResolvePage("LaunchPage"));

        // Mica 只在 Windows 11 上有效。
        // 老系统上 TransparencyLevelHint 会被忽略，而我们同时把 Background 设成了
        // Transparent —— 结果就是窗口完全不着色、内容还没画出来时一片「白/黑屏」。
        // 用 AppEnvironment（注册表探测）而不是 Window.IsWindows11：后者依赖系统谎报的
        // Environment.OSVersion，在 Win7 上也会返回「不是 Win11」，但 Win10 上同样判不出来，
        // 统一走我们自己的探测更可靠。
        if (AppEnvironment.IsWindows11
            && !perfMode
            && ActualThemeVariant != FluentAvaloniaTheme.HighContrastTheme)
        {
            TryEnableMicaEffect();
        }
    }

    /// <summary>取得 AppRoot 的缩放 / 位移变换（与 AnimationHelper 用同一套结构）。</summary>
    private (ScaleTransform Scale, TranslateTransform Translate) EnsureAppRootTransform()
    {
        if (AppRoot.RenderTransform is TransformGroup group
            && group.Children.Count >= 2
            && group.Children[0] is ScaleTransform scale
            && group.Children[1] is TranslateTransform translate)
        {
            return (scale, translate);
        }

        var newScale = new ScaleTransform(1, 1);
        var newTranslate = new TranslateTransform(0, 0);
        AppRoot.RenderTransform = new TransformGroup { Children = { newScale, newTranslate } };
        return (newScale, newTranslate);
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

    /// <summary>给侧边栏所有导航项挂上「按下回缩 / 松开回弹」的点击反馈。</summary>
    private void AttachMenuItemFeedback()
    {
        foreach (var item in EnumerateMenuItems(RootNavigation.MenuItems)
                     .Concat(EnumerateMenuItems(RootNavigation.FooterMenuItems)))
        {
            InteractionFeedback.AttachPressFeedback(item);
        }
    }

    private static System.Collections.Generic.IEnumerable<FANavigationViewItem> EnumerateMenuItems(
        System.Collections.IEnumerable? items)
    {
        if (items is null)
            yield break;

        foreach (var entry in items)
        {
            if (entry is not FANavigationViewItem navItem)
                continue;

            yield return navItem;

            // 导航项支持嵌套子项，一并覆盖
            foreach (var child in EnumerateMenuItems(navItem.MenuItems))
                yield return child;
        }
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
        if (!AppEnvironment.IsWindows11)
            return;

        if (SettingsService.Current.PerformanceMode)
        {
            // 性能模式：彻底不碰窗口底色，保持 XAML 里声明的纯色背景
            ClearValue(BackgroundProperty);
            ClearValue(TransparencyBackgroundFallbackProperty);
            return;
        }

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
        // ⚠️「换台电脑整窗透明」的根因（1.0.6.11）：
        // 之前这里无条件把 Background 设成 Transparent + hint Mica，**假设 Mica 一定生效**。
        // 但 Mica 能不能渲染由 DWM 决定：系统关了「透明效果」、远程桌面、部分虚拟机/驱动下，
        // Avalonia 请求 Mica 会**回退**到 Transparent（分层窗口全 alpha 合成）甚至 None，
        // 而 AppRoot 没有任何不透明背景（XAML 注释还写着「未设置壁纸时保持纯 Mica 底」）
        // → 整窗连同自定义标题栏一起**穿透到桌面**。
        //
        // 修法：hint 照发，但**背景是否透明以实际达成的 TransparencyLevel 为准**
        // （TransparencyLevelChanged + Opened 后各校一次）；回退瞬间用不透明 fallback 顶着。
        TransparencyBackgroundFallback = ResolveOpaqueWindowBrush() ?? Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica };
        UpdateMicaBackground();
    }

    /// <summary>
    /// 按实际透明级别决定窗口底色：只有 Mica / AcrylicBlur 真的生效才透明；
    /// 否则清掉局部值，让 XAML 的 <c>AppWindowBackgroundBrush</c>（不透明，ApplyTheme 覆写）回来。
    /// </summary>
    private void UpdateMicaBackground()
    {
        // ActualTransparencyLevel 是可空枚举，用等值比较而不是 is 常量模式（CS9135）。
        if (ActualTransparencyLevel == WindowTransparencyLevel.Mica
            || ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur)
        {
            Background = Brushes.Transparent;
        }
        else
        {
            ClearValue(BackgroundProperty);
        }
    }

    /// <summary>取当前主题下不透明的窗口底色（AppWindowBackgroundBrush，ApplyTheme 保证不透明）。</summary>
    private IBrush? ResolveOpaqueWindowBrush() =>
        Application.Current?.TryFindResource("AppWindowBackgroundBrush", ActualThemeVariant, out var v) == true
        && v is IBrush brush
            ? brush
            : null;

    // ==================== 拖入整合包 ====================

    /// <summary>
    /// 注册「把整合包拖进窗口就能装」。
    ///
    /// <para>
    /// 事件挂在窗口本身（<c>AddHandler</c> 用隧道前的冒泡阶段）。注意标题栏区域已被
    /// <c>WindowDecorationProperties.ElementRole="TitleBar"</c> 标成拖拽区，但它只接管
    /// **文件管理器拖窗**，不影响文件拖放事件 —— 文件拖放走的是 Avalonia 自己的 DragDrop 通道，
    /// 两者不冲突（实测在标题栏上松手也能拿到）。
    /// </para>
    ///
    /// <para>
    /// <b>只在「第一个文件是整合包」时才接受拖放</b>：如果无脑接受，用户拖一个普通文件进来会看到
    /// 覆盖层闪一下再报「无法识别的格式」，观感很差；先判断再决定 <c>e.DragEffects</c>，
    /// 不匹配时显示「禁止」光标，符合系统习惯。
    /// </para>
    /// </summary>
    private void SetUpDragDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragEnter(object? sender, DragEventArgs e) => UpdateDropFeedback(e);

    private void OnDragOver(object? sender, DragEventArgs e) => UpdateDropFeedback(e);

    private void OnDragLeave(object? sender, DragEventArgs e) => HideDropOverlay();

    /// <summary>根据拖进来的东西是不是整合包，切换光标效果与覆盖层。</summary>
    private void UpdateDropFeedback(DragEventArgs e)
    {
        var paths = GetDroppedPaths(e);
        var modpack = paths.FirstOrDefault(p => ModpackInstaller.LooksLikeModpack(p));

        if (modpack is null)
        {
            e.DragEffects = DragDropEffects.None;
            HideDropOverlay();
            return;
        }

        e.DragEffects = DragDropEffects.Copy;

        // 已经在装了就别再显示「松开即可安装」，否则用户松手后只等到一句「正在安装」
        if (ModpackInstallCoordinator.IsRunning)
        {
            DropOverlayTitle.Text = "已有整合包正在安装";
            DropOverlayHint.Text = "请等待当前安装完成后再拖入新的整合包";
        }
        else
        {
            DropOverlayTitle.Text = "松开即可安装整合包";
            DropOverlayHint.Text = Path.GetFileName(modpack);
        }

        DropOverlay.IsVisible = true;
    }

    private void HideDropOverlay() => DropOverlay.IsVisible = false;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        HideDropOverlay();

        var paths = GetDroppedPaths(e);
        if (paths.Count == 0)
            return;

        e.DragEffects = DragDropEffects.Copy;

        var modpack = paths.FirstOrDefault(p => ModpackInstaller.LooksLikeModpack(p));
        if (modpack is null)
        {
            AppServices.Notifications?.Warning(
                "拖入的文件不是整合包。支持 Modrinth 的 .mrpack，以及带 manifest.json 的 CurseForge .zip");
            return;
        }

        // 拖了多个文件时，剩下的可能是普通文件；只要有一个是整合包就装它。
        await InstallDroppedModpackAsync(modpack);
    }

    /// <summary>从拖放数据里取出本地文件路径（过滤掉非文件项）。</summary>
    private static System.Collections.Generic.List<string> GetDroppedPaths(DragEventArgs e)
    {
        var result = new System.Collections.Generic.List<string>();

        // Avalonia 12：拖放数据从 e.Data(DataObject) 换成了 e.DataTransfer(IDataTransfer)，
        // 取文件要走 DataTransferExtensions.TryGetFiles，拿到的还是 IStorageItem。
        Avalonia.Platform.Storage.IStorageItem[]? files;
        try
        {
            files = e.DataTransfer?.TryGetFiles();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取拖放文件列表失败");
            return result;
        }

        if (files is null)
            return result;

        foreach (var item in files)
        {
            var path = item.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                result.Add(path!);
        }

        return result;
    }

    /// <summary>
    /// 拖入后真正开始安装：交给协调器（与下载页入口共用同一套流程与提示）。
    /// 装完刷新一次启动页，让新实例出现在下拉框里。
    /// </summary>
    private async Task InstallDroppedModpackAsync(string path)
    {
        if (ModpackInstallCoordinator.IsRunning)
        {
            AppServices.Notifications?.Warning("已有整合包正在安装，请等它完成后再试");
            return;
        }

        var instanceId = await ModpackInstallCoordinator.InstallAsync(path, "拖入的文件");
        if (instanceId is null)
            return;

        // 让「启动」页重新读一遍实例列表（它可能正开着，缓存的是旧列表）
        RefreshLaunchPageIfVisible();
    }

    /// <summary>
    /// 若当前停在启动页，通知它重新枚举实例（新装的整合包实例要立刻能选）。
    /// 用消息而不是直接持有 VM 引用，避免主窗口依赖某个页面的内部结构。
    /// </summary>
    private void RefreshLaunchPageIfVisible()
    {
        try
        {
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default
                .Send<Models.InstancesChangedMessage, string>(new Models.InstancesChangedMessage(), "InstancesChanged");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "通知实例列表刷新失败（不影响安装结果）");
        }
    }

    // ==================== 内嵌子页宿主（1.0.8.0） ====================

    /// <summary>
    /// 当前主窗口。内嵌子页要往它的内容区里塞控件，而 ViewModel / Service
    /// 层拿不到窗口实例，所以留一个静态引用（只此一处，别扩散）。
    /// </summary>
    internal static MainWindow? Instance { get; private set; }

    internal void ShowInline(Control content)
    {
        InlineContentHost.Content = content;
        InlineHostLayer.IsVisible = true;

        // 子页出现时把焦点交给它：否则键盘焦点还停在被盖住的导航项上，
        // Tab / 方向键会操作看不见的控件。
        content.Focus();
    }

    internal void HideInline()
    {
        InlineHostLayer.IsVisible = false;
        InlineContentHost.Content = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        // 主窗口关了，内嵌页的 await 不能永远挂着
        Services.InlineHost.Close();
        Instance = null;
        base.OnClosed(e);
    }

}
