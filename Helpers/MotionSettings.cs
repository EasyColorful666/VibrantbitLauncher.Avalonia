using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 全应用动效的开关（减动效 / reduce-motion 的统一入口）。
///
/// <para>
/// 本工程的「减动效」不是另做一个开关，而是复用设置里的**性能模式**：
/// 低配机器（Win7 x86 老机器、核显笔记本）上动画一卡就会长时间停在中途，
/// 观感比没有动画更糟。所以性能模式同时承担两件事 —— 关视觉效果，也关动效。
/// </para>
///
/// <para>
/// <b>为什么需要这个类</b>：动效散落在三处（窗口、页面、列表），
/// 每处各自读 <see cref="SettingsService.Current"/> 的话，
/// 以后要加「只关动效不关壁纸」这种开关就得改三个地方。
/// 统一走这里，只改一行。
/// </para>
///
/// <para>
/// XAML 侧对应的关闭规则写在 <c>Styles/Animations.axaml</c> 的
/// <c>.performanceMode</c> 系列样式里（MainWindow 会在根节点挂这个类）。
/// </para>
/// </summary>
public static class MotionSettings
{
    /// <summary>是否允许播放动效。</summary>
    public static bool Enabled => !SettingsService.Current.PerformanceMode;

    /// <summary>是否需要禁用过渡（与 <see cref="Enabled"/> 相反，给 XAML / 外部调用更直观的语义）。</summary>
    public static bool DisableTransitions => !Enabled;
}
