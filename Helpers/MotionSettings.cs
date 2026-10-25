using System;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 全应用动效的统一闸门。
///
/// <para>
/// 档位来自「外观 → 动效」，三档：完整 / 精简 / 关闭。性能模式是更粗的总开关，
/// 它开着时一律按「关闭」处理 —— 低配机器上动画一卡就会长时间停在中途，
/// 观感比没有动画更糟。
/// </para>
///
/// <para>
/// <b>为什么需要这个类</b>：动效散落在三处（窗口、页面、列表），
/// 每处各自读 <see cref="SettingsService.Current"/> 的话，
/// 以后要加「只关动效不关壁纸」这种开关就得改三个地方。统一走这里，只改一行。
/// </para>
///
/// <para>
/// XAML 侧对应的关闭规则写在 <c>Styles/Animations.axaml</c> 的
/// <c>.performanceMode</c> 系列样式里（MainWindow 会在根节点挂这个类）。
/// </para>
/// </summary>
public static class MotionSettings
{
    /// <summary>当前生效的档位：Full / Reduced / Off。</summary>
    public static string Level =>
        SettingsService.Current.PerformanceMode ? "Off" : SettingsService.AnimationLevel;

    /// <summary>是否允许播放动效。</summary>
    public static bool Enabled => Level != "Off";

    /// <summary>是否需要禁用过渡（与 <see cref="Enabled"/> 相反，给 XAML / 外部调用更直观的语义）。</summary>
    public static bool DisableTransitions => !Enabled;

    /// <summary>
    /// 精简档：保留「有 / 没有」的反馈，砍掉位移与缩放。
    ///
    /// <para>
    /// 判断依据是「这一下动画是否在表达状态变化」：按钮按下变暗是反馈，留着；
    /// 卡片浮起、页面滑入是装饰，去掉。
    /// </para>
    /// </summary>
    public static bool Reduced => Level == "Reduced";

    /// <summary>动画时长倍率：速度越快，时长越短。手写动画的时长都要乘它。</summary>
    public static double DurationScale => 1.0 / SettingsService.AnimationSpeed;

    /// <summary>页面切换方式：Slide / Fade / None（关掉动效时强制 None）。</summary>
    public static string PageTransition =>
        Enabled ? SettingsService.PageTransitionMode : "None";

    /// <summary>把基准时长按当前速度缩放。</summary>
    public static TimeSpan Scale(TimeSpan baseline) =>
        TimeSpan.FromMilliseconds(baseline.TotalMilliseconds * DurationScale);
}
