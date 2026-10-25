using Avalonia.Controls;

namespace VibrantbitLauncher.Controls;

/// <summary>
/// 极光背景：四块巨大的径向渐变，叠在窗口最底层做环境材质。
///
/// <para>
/// 只有 XAML，没有逻辑 —— 它的全部价值在"静态"，任何逐帧动画都会
/// 让低端机器为一张背景付出十几毫秒的每帧代价（原因写在 axaml 的注释里）。
/// </para>
/// </summary>
public partial class AuroraBackground : UserControl
{
    public AuroraBackground()
    {
        InitializeComponent();
    }
}
