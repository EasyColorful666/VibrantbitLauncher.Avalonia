using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace VibrantbitLauncher.Controls;

/// <summary>
/// 自定义标题栏，造型对齐 WPF-UI（lepo.co/wpfui）的 <c>ui:TitleBar</c>：
/// 左侧「应用图标 + 标题（可带副标题）+ 自定义内容」，右侧自带
/// 最小化 / 最大化（还原）/ 关闭 三颗系统级按钮。
///
/// <para>
/// <b>为什么自己画按钮：</b>Avalonia / FluentAvalonia 都没有提供「可完全自绘」的标题栏控件。
/// <c>FluentAvalonia.UI.Windowing.FAAppWindow</c> 用的是**系统原生**的三颗按钮（DWM 绘制），
/// 而且它显式禁止外部改 <c>ExtendClientAreaToDecorationsHint</c>（改了直接抛异常），
/// 所以想拿到和 WPF-UI 一致的「自绘按钮 + 统一交互」就只能自己写一个。
/// </para>
///
/// <para>
/// <b>用法：</b>把它放到窗口内容的最上面一行即可，不需要在窗口上写任何额外属性 ——
/// 控件挂到可视化树时会自动把宿主窗口切成
/// <see cref="WindowDecorations.BorderOnly"/>（保留系统边框 / 阴影 / 缩放，去掉系统标题栏），
/// 并把 <see cref="TopLevel.ExtendClientAreaToDecorationsHint"/> 打开，
/// 让 <c>WindowDecorationProperties.ElementRole</c> 的拖拽区标记生效。
/// 不想要这套自动接管就设 <see cref="AutoConfigureWindow"/>=false。
/// </para>
///
/// <para>
/// <b>拖拽与双击：</b>模板根节点标了 <c>ElementRole="TitleBar"</c>，平台会把它当非客户区处理，
/// 拖动窗口 / 双击最大化 / 右键系统菜单都由系统接管（连带 Aero Snap 也在）。
/// 但 Role 只在 <c>ExtendClientAreaToDecorationsHint</c> 真正生效时才有用，
/// 所以这里额外做了一份**代码兜底**：若发现平台没接管，就自己 <c>BeginMoveDrag</c> + 双击切最大化。
/// 两条路径互斥（平台接管时指针事件根本到不了托管层），不会重复触发。
/// </para>
/// </summary>
[TemplatePart(PartIcon, typeof(Image))]
[TemplatePart(PartTitle, typeof(TextBlock))]
[TemplatePart(PartSubtitle, typeof(TextBlock))]
[TemplatePart(PartMinimizeButton, typeof(Button))]
[TemplatePart(PartMaximizeButton, typeof(Button))]
[TemplatePart(PartCloseButton, typeof(Button))]
[TemplatePart(PartMaximizeGlyph, typeof(Visual))]
[TemplatePart(PartRestoreGlyph, typeof(Visual))]
[PseudoClasses(PseudoMaximized)]
public class TitleBar : ContentControl
{
    public const string PartIcon = "PART_Icon";
    public const string PartTitle = "PART_Title";
    public const string PartSubtitle = "PART_Subtitle";
    public const string PartMinimizeButton = "PART_MinimizeButton";
    public const string PartMaximizeButton = "PART_MaximizeButton";
    public const string PartCloseButton = "PART_CloseButton";
    public const string PartMaximizeGlyph = "PART_MaximizeGlyph";
    public const string PartRestoreGlyph = "PART_RestoreGlyph";

    private const string PseudoMaximized = ":maximized";

    // ==================== 属性 ====================

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<TitleBar, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<TitleBar, string?>(nameof(Subtitle));

    public static readonly StyledProperty<IImage?> IconProperty =
        AvaloniaProperty.Register<TitleBar, IImage?>(nameof(Icon));

    /// <summary>
    /// 这条标题栏是不是「被内嵌到主窗口里的子页标题栏」。
    ///
    /// <para>
    /// 内嵌的子页仍然带着自己原来那个窗口的标题栏（PCL2 就是这个观感），但它挂在
    /// 主窗口的可视树里 —— 如果照原来的逻辑 Hook 宿主窗口，点 × 会把整个启动器关掉，
    /// 最小化/最大化也会作用到主窗口上。所以内嵌页必须显式标 IsInline="True"：
    /// 不 Hook 窗口、隐藏最小化与最大化、× 改成关闭这个内嵌页。
    /// </para>
    /// </summary>
    public static readonly StyledProperty<bool> IsInlineProperty =
        AvaloniaProperty.Register<TitleBar, bool>(nameof(IsInline));

    public static readonly StyledProperty<bool> ShowMinimizeProperty =
        AvaloniaProperty.Register<TitleBar, bool>(nameof(ShowMinimize), true);

    public static readonly StyledProperty<bool> ShowMaximizeProperty =
        AvaloniaProperty.Register<TitleBar, bool>(nameof(ShowMaximize), true);

    public static readonly StyledProperty<bool> ShowCloseProperty =
        AvaloniaProperty.Register<TitleBar, bool>(nameof(ShowClose), true);

    /// <summary>窗口当前是否最大化（只读语义，由控件跟随窗口状态写入）。</summary>
    public static readonly StyledProperty<bool> IsMaximizedProperty =
        AvaloniaProperty.Register<TitleBar, bool>(nameof(IsMaximized));

    /// <summary>宿主窗口是否允许最大化；不允许时最大化按钮置灰（与系统标题栏一致）。</summary>
    public static readonly StyledProperty<bool> CanMaximizeProperty =
        AvaloniaProperty.Register<TitleBar, bool>(nameof(CanMaximize), true);

    /// <summary>
    /// 是否自动接管宿主窗口的装饰：切成 BorderOnly + 打开 ExtendClientAreaToDecorationsHint。
    /// 想自己控制窗口装饰就设成 false。
    /// </summary>
    public static readonly StyledProperty<bool> AutoConfigureWindowProperty =
        AvaloniaProperty.Register<TitleBar, bool>(nameof(AutoConfigureWindow), true);

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public IImage? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public bool IsInline
    {
        get => GetValue(IsInlineProperty);
        set => SetValue(IsInlineProperty, value);
    }

    public bool ShowMinimize
    {
        get => GetValue(ShowMinimizeProperty);
        set => SetValue(ShowMinimizeProperty, value);
    }

    public bool ShowMaximize
    {
        get => GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    public bool ShowClose
    {
        get => GetValue(ShowCloseProperty);
        set => SetValue(ShowCloseProperty, value);
    }

    public bool IsMaximized
    {
        get => GetValue(IsMaximizedProperty);
        set => SetValue(IsMaximizedProperty, value);
    }

    public bool CanMaximize
    {
        get => GetValue(CanMaximizeProperty);
        set => SetValue(CanMaximizeProperty, value);
    }

    public bool AutoConfigureWindow
    {
        get => GetValue(AutoConfigureWindowProperty);
        set => SetValue(AutoConfigureWindowProperty, value);
    }

    static TitleBar()
    {
        IconProperty.Changed.AddClassHandler<TitleBar>((x, _) => x.UpdateIcon());
        IsMaximizedProperty.Changed.AddClassHandler<TitleBar>((x, _) => x.UpdateMaximizeVisual());
        ShowMinimizeProperty.Changed.AddClassHandler<TitleBar>((x, _) => x.UpdateButtonVisibility());
        ShowMaximizeProperty.Changed.AddClassHandler<TitleBar>((x, _) => x.UpdateButtonVisibility());
        ShowCloseProperty.Changed.AddClassHandler<TitleBar>((x, _) => x.UpdateButtonVisibility());
        IsInlineProperty.Changed.AddClassHandler<TitleBar>((x, _) => x.OnIsInlineChanged());
        CanMaximizeProperty.Changed.AddClassHandler<TitleBar>((x, _) => x.UpdateButtonVisibility());
        SubtitleProperty.Changed.AddClassHandler<TitleBar>((x, _) => x.UpdateSubtitle());
    }

    // ==================== 模板部件 ====================

    private Window? _window;
    private bool _hooked;

    private Image? _icon;
    private TextBlock? _subtitle;
    private Button? _minimizeButton;
    private Button? _maximizeButton;
    private Button? _closeButton;
    private Visual? _maximizeGlyph;
    private Visual? _restoreGlyph;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        DetachParts();

        _icon = e.NameScope.Find<Image>(PartIcon);
        _subtitle = e.NameScope.Find<TextBlock>(PartSubtitle);
        _minimizeButton = e.NameScope.Find<Button>(PartMinimizeButton);
        _maximizeButton = e.NameScope.Find<Button>(PartMaximizeButton);
        _closeButton = e.NameScope.Find<Button>(PartCloseButton);
        _maximizeGlyph = e.NameScope.Find<Visual>(PartMaximizeGlyph);
        _restoreGlyph = e.NameScope.Find<Visual>(PartRestoreGlyph);

        if (_minimizeButton is not null)
            _minimizeButton.Click += OnMinimizeClick;
        if (_maximizeButton is not null)
            _maximizeButton.Click += OnMaximizeClick;
        if (_closeButton is not null)
            _closeButton.Click += OnCloseClick;

        UpdateIcon();
        UpdateSubtitle();
        UpdateButtonVisibility();
        UpdateMaximizeVisual();
    }

    private void DetachParts()
    {
        if (_minimizeButton is not null)
            _minimizeButton.Click -= OnMinimizeClick;
        if (_maximizeButton is not null)
            _maximizeButton.Click -= OnMaximizeClick;
        if (_closeButton is not null)
            _closeButton.Click -= OnCloseClick;

        _icon = null;
        _subtitle = null;
        _minimizeButton = null;
        _maximizeButton = null;
        _closeButton = null;
        _maximizeGlyph = null;
        _restoreGlyph = null;
    }

    // ==================== 宿主窗口 ====================

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookWindow(TopLevel.GetTopLevel(this) as Window);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnhookWindow();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 允许在窗口构造阶段（还没进可视化树）之外，由窗口自己把宿主塞进来。
    /// 正常情况不用调，挂到树上时会自动认领。
    /// </summary>
    public void AttachTo(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        HookWindow(window);
    }

    private void HookWindow(Window? window)
    {
        // 内嵌页的标题栏不能操作主窗口：一旦 Hook 上，× 会把整个启动器关掉
        if (IsInline)
            window = null;

        if (ReferenceEquals(_window, window))
            return;

        UnhookWindow();
        _window = window;

        if (window is null)
            return;

        if (AutoConfigureWindow)
            ConfigureWindow(window);

        // 按钮可用性跟随窗口能力（固定尺寸的对话框最大化钮要置灰）
        CanMaximize = window.CanMaximize;
        IsMaximized = window.WindowState == WindowState.Maximized;
        UpdateButtonVisibility();
        UpdateMaximizeVisual();

        window.PropertyChanged += OnWindowPropertyChanged;
        _hooked = true;
    }

    private void UnhookWindow()
    {
        if (_hooked && _window is not null)
            _window.PropertyChanged -= OnWindowPropertyChanged;

        _hooked = false;
        _window = null;
    }

    /// <summary>
    /// 把窗口切成「只有边框、没有系统标题栏」：系统继续负责边框 / 圆角 / 阴影 / 缩放命中，
    /// 标题栏部分交给我们。再打开 ExtendClientAreaToDecorationsHint，
    /// 否则 <c>ElementRole="TitleBar"</c> 的拖拽标记无效。
    /// </summary>
    private static void ConfigureWindow(Window window)
    {
        // ⚠️ 一定要判 Full 再改：有些窗口（或将来某个基类）可能已经自己定好了装饰级别，
        // 无脑覆盖会把对方的窗口样式带坏。
        if (window.WindowDecorations == WindowDecorations.Full)
            window.WindowDecorations = WindowDecorations.BorderOnly;

        // ⚠️ FAAppWindow 在 Windows 上把这个属性的默认值改成 true，且**改了就抛异常**，
        // 所以只在它为 false 时写一次，避免误伤。
        if (!window.ExtendClientAreaToDecorationsHint)
            window.ExtendClientAreaToDecorationsHint = true;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            IsMaximized = _window?.WindowState == WindowState.Maximized;
        }
        else if (e.Property == Window.CanMaximizeProperty)
        {
            CanMaximize = _window?.CanMaximize ?? false;
        }
    }

    // ==================== 窗口操作 ====================

    /// <summary>最小化窗口。</summary>
    public void Minimize()
    {
        if (_window is { CanMinimize: true } window)
            window.WindowState = WindowState.Minimized;
    }

    /// <summary>在最大化 / 还原之间切换。</summary>
    public void ToggleMaximize()
    {
        if (_window is not { } window || !window.CanMaximize)
            return;

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    /// <summary>关闭窗口；内嵌页则是关闭当前内嵌子页。</summary>
    public void CloseWindow()
    {
        if (IsInline)
        {
            Services.InlineHost.Close();
            return;
        }

        _window?.Close();
    }

    private void OnIsInlineChanged()
    {
        // 切成内嵌模式时立刻放开宿主窗口的钩子
        if (IsInline)
            UnhookWindow();

        UpdateButtonVisibility();
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        Minimize();
    }

    private void OnMaximizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        ToggleMaximize();
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        CloseWindow();
    }

    // ==================== 指针：拖拽 / 双击兜底 ====================

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // 平台若接管了非客户区（ElementRole=TitleBar 生效），指针事件根本到不了托管层；
        // 能走到这里就说明没接管（实测：窗口切成 BorderOnly 后，Windows 上
        // Avalonia 的 ElementRole 拖拽区并未生效），那就自己拖。
        if (e.Handled || _window is null)
            return;

        if (IsInteractive(e.Source))
            return;

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            e.Handled = true;

            // ⚠️ 双击最大化必须在**起拖之前**判掉：BeginMoveDrag 会进入系统级的模态移动循环，
            // 一次「按下-抬起」被它整段吃掉，等它返回时双击手势已经被消费，
            // OnDoubleTapped 永远不会触发（实测：只靠 OnDoubleTapped 双击无效）。
            if (e.ClickCount >= 2)
            {
                ToggleMaximize();
                return;
            }

            try
            {
                _window.BeginMoveDrag(e);
            }
            catch (Exception ex)
            {
                // 个别平台 / 无窗口管理器环境下会抛，拖不动不该让整窗崩掉
                Serilog.Log.Debug(ex, "标题栏拖拽失败");
            }
        }
    }

    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);

        if (e.Handled || IsInteractive(e.Source))
            return;

        e.Handled = true;
        ToggleMaximize();
    }

    /// <summary>判断这次点击是否落在按钮 / 输入控件上（是的话就别抢去拖窗口）。</summary>
    private bool IsInteractive(object? source)
    {
        if (source is not Visual visual)
            return false;

        for (Visual? current = visual; current is not null && !ReferenceEquals(current, this); current = current.GetVisualParent())
        {
            if (current is Button or TextBox or ComboBox or Slider or ScrollBar or ToggleSwitch)
                return true;
        }

        return false;
    }

    // ==================== 视觉同步 ====================

    private void UpdateIcon()
    {
        if (_icon is null)
            return;

        _icon.Source = Icon;
        _icon.IsVisible = Icon is not null;
    }

    private void UpdateSubtitle()
    {
        if (_subtitle is null)
            return;

        _subtitle.IsVisible = !string.IsNullOrEmpty(Subtitle);
    }

    private void UpdateButtonVisibility()
    {
        if (_minimizeButton is not null)
            _minimizeButton.IsVisible = ShowMinimize && !IsInline;
        if (_maximizeButton is not null)
            _maximizeButton.IsVisible = ShowMaximize && !IsInline;
        if (_closeButton is not null)
            _closeButton.IsVisible = ShowClose;

        // 最大化按钮置灰而不是隐藏 —— 跟 Windows 原生标题栏对固定尺寸窗口的处理一致
        if (_maximizeButton is not null)
            _maximizeButton.IsEnabled = CanMaximize;
    }

    private void UpdateMaximizeVisual()
    {
        PseudoClasses.Set(PseudoMaximized, IsMaximized);

        if (_maximizeGlyph is not null)
            _maximizeGlyph.IsVisible = !IsMaximized;
        if (_restoreGlyph is not null)
            _restoreGlyph.IsVisible = IsMaximized;
    }
}
