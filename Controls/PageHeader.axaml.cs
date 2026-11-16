using Avalonia;
using Avalonia.Controls;

namespace VibrantbitLauncher.Controls;

/// <summary>
/// 一级页面的页头：眉标 + 大标题 + 主色短横 + 说明，右侧留一个操作槽。
///
/// <para>
/// 15 个页面原本各写一遍这套结构，字号与间距已经漂移出三种写法。
/// 收成一个控件后，改一次全站生效。
/// </para>
///
/// <para>
/// 不用 XAML 绑定，而是属性变更时直接写控件：
/// UserControl 自己的 <c>x:Name</c> 不会进入同名 namescope，
/// <c>ElementName</c> / <c>$parent</c> 这种反射绑定失败时是静默的，
/// 结果就是"所有页面标题凭空消失"且没有任何日志。
/// </para>
public partial class PageHeader : UserControl
{
    /// <summary>标题上方的小字，用来交代"这是哪一区"，可空。</summary>
    public static readonly StyledProperty<string?> EyebrowProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Eyebrow));

    /// <summary>页面主标题。</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Title));

    /// <summary>标题下方的一句说明，可空。</summary>
    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Subtitle));

    /// <summary>
    /// 右侧操作槽（刷新 / 新建 / 筛选）。
    ///
    /// <para>
    /// 单独开一个属性而不是复用 Content：UserControl 的 Content 就是页头这段 XAML 本身，
    /// 把内容宿主绑到它上面会形成自引用。
    /// </para>
    /// </summary>
    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<PageHeader, object?>(nameof(Actions));

    static PageHeader()
    {
        EyebrowProperty.Changed.AddClassHandler<PageHeader>((header, _) => header.SyncEyebrow());
        TitleProperty.Changed.AddClassHandler<PageHeader>((header, _) => header.SyncTitle());
        SubtitleProperty.Changed.AddClassHandler<PageHeader>((header, _) => header.SyncSubtitle());
        ActionsProperty.Changed.AddClassHandler<PageHeader>((header, _) => header.SyncActions());
    }

    public PageHeader()
    {
        InitializeComponent();

        // XAML 里的默认值不会触发 Changed，这里补一次首帧同步
        SyncEyebrow();
        SyncTitle();
        SyncSubtitle();
        SyncActions();
    }

    public string? Eyebrow
    {
        get => GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

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

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    private void SyncEyebrow()
    {
        var text = Eyebrow;
        PART_Eyebrow.Text = text;
        PART_Eyebrow.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private void SyncTitle() => PART_Title.Text = Title;

    private void SyncSubtitle()
    {
        var text = Subtitle;
        PART_Subtitle.Text = text;
        PART_Subtitle.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private void SyncActions() => PART_Actions.Content = Actions;

    /// <summary>
    /// 每次页面被重新挂上视觉树时重播一次入场（标题从下方 8px 落定）。
    ///
    /// <para>
    /// 页面是 DI 单例，来回切页拿到的是同一个实例，样式类不会自己"重新生效"——
    /// 所以先摘掉类、等这一帧布局跑完再挂回去，动画才会重新创建。
    /// 关掉动效 / 精简档时根本不加这个类（见 Animations.axaml 的 <c>ctrl|PageHeader.riseIn</c>）。
    /// </para>
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (!Helpers.MotionSettings.AllowMovement)
        {
            Classes.Remove("riseIn");
            return;
        }

        Classes.Remove("riseIn");
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => Classes.Add("riseIn"),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }
}
