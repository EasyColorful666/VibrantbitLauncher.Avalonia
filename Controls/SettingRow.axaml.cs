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
/// </summary>
public partial class SettingRow : UserControl
{
    /// <summary>行标题。</summary>
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Header));

    /// <summary>标题下方的一句说明，可空。</summary>
    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    /// <summary>右侧的控件。</summary>
    public static readonly StyledProperty<object?> BodyProperty =
        AvaloniaProperty.Register<SettingRow, object?>(nameof(Body));

    static SettingRow()
    {
        HeaderProperty.Changed.AddClassHandler<SettingRow>((row, _) => row.SyncHeader());
        DescriptionProperty.Changed.AddClassHandler<SettingRow>((row, _) => row.SyncDescription());
        BodyProperty.Changed.AddClassHandler<SettingRow>((row, _) => row.SyncBody());
    }

    public SettingRow()
    {
        InitializeComponent();

        // XAML 里写死的属性不会触发 Changed，这里补一次首帧同步
        SyncHeader();
        SyncDescription();
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

    private void SyncBody() => PART_Body.Content = Body;
}
