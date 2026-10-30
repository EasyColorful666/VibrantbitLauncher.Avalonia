using Avalonia;
using Avalonia.Controls;
using Avalonia.Metadata;

namespace VibrantbitLauncher.Controls;

/// <summary>
/// 设置页的一行：左侧标题与说明，右侧控件。
///
/// <para>
/// 用 <see cref="Body"/> 作为内容属性，所以页面里直接写子元素就能落到右侧，
/// 不需要每次都套一层 &lt;ctrl:SettingRow.Body&gt;。
/// </para>
///
/// <para>
/// 不用 XAML 绑定，而是属性变更时直接写控件：
/// <c>$parent[自定义类型]</c> 的反射绑定在运行时解析不到类型会直接抛异常，
/// 换成 <c>$parent[UserControl]</c> 又会因为 UserControl 上没有 Header 而静默失败，
/// 结果是整页设置项一片空白且没有任何日志。与 <see cref="PageHeader"/> 同一套做法。
/// </para>
///
/// <para>
/// <see cref="Badge"/> 是标题右上角那个小胶囊，用来标「重启后生效」这类补充信息。
/// 只有需要额外说明的行才写，没写的行完全看不出差别（胶囊直接隐藏，不占位）。
/// </para>
/// </summary>
public partial class SettingRow : UserControl
{
    /// <summary>行标题。</summary>
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Header));

    /// <summary>标题下方的一句说明，可空。</summary>
    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    /// <summary>标题右侧的小标签，如「重启后生效」。空表示不显示。</summary>
    public static readonly StyledProperty<string?> BadgeProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Badge));

    /// <summary>右侧的控件。</summary>
    public static readonly StyledProperty<object?> BodyProperty =
        AvaloniaProperty.Register<SettingRow, object?>(nameof(Body));

    static SettingRow()
    {
        HeaderProperty.Changed.AddClassHandler<SettingRow>((row, _) => row.SyncHeader());
        DescriptionProperty.Changed.AddClassHandler<SettingRow>((row, _) => row.SyncDescription());
        BadgeProperty.Changed.AddClassHandler<SettingRow>((row, _) => row.SyncBadge());
        BodyProperty.Changed.AddClassHandler<SettingRow>((row, _) => row.SyncBody());
    }

    public SettingRow()
    {
        InitializeComponent();

        // XAML 里写死的属性不会触发 Changed，这里补一次首帧同步
        SyncHeader();
        SyncDescription();
        SyncBadge();
        SyncBody();
    }

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? Badge
    {
        get => GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    [Content]
    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    private void SyncHeader() => PART_Header.Text = Header;

    private void SyncDescription()
    {
        var text = Description;
        PART_Description.Text = text;

        // 没有说明时整行收起，否则标题下面会留一段空白
        PART_Description.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private void SyncBadge()
    {
        var text = Badge;
        PART_BadgeText.Text = text;

        // 空标签连外框一起收掉 —— 否则每行标题右边都会多出一个小圆点
        PART_Badge.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private void SyncBody() => PART_Body.Content = Body;
}
