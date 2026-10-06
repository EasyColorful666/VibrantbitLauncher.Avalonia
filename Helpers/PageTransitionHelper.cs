using System;
using Avalonia;
using Avalonia.Controls;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>
    /// 轻量入场动效的统一入口。
    ///
    /// 下载中心（左侧分类）、设置页（左侧分类）的分栏切换，以及主窗口首次显示，
    /// 都走这里，页面不必各写一份动画代码；具体实现统一在
    /// <see cref="AnimationHelper"/> 里，节奏（时长 / 缓动 / 位移量）全应用一致。
    /// </summary>
    public static class PageTransitionHelper
    {
        /// <summary>
        /// 让 <paramref name="element"/> 播一次入场动画（上浮 18px + 淡入 + 轻微推近，约 260ms）。
        /// </summary>
        public static void PlayEnterTransition(Visual? element)
        {
            if (element is not Control control)
                return;

            _ = AnimationHelper.AnimateAsync(
                control,
                fromOpacity: 0d, toOpacity: 1d,
                fromScale: 0.985d, toScale: 1d,
                fromOffsetY: 18d, toOffsetY: 0d,
                TimeSpan.FromMilliseconds(260));
        }

        /// <summary>
        /// 主窗口内容入场：整体淡入 + 从 98.5% 推到 100%。
        ///
        /// 窗口底色是 Mica（透明），这一下就像内容从系统材质里浮出来；
        /// 时长给到 360ms，比页面切换更缓，避免「窗口和页面同时动」显得吵。
        /// </summary>
        public static void PlayWindowEntrance(Visual? element)
        {
            if (element is not Control control)
                return;

            _ = AnimationHelper.AnimateAsync(
                control,
                fromOpacity: 0d, toOpacity: 1d,
                fromScale: 0.985d, toScale: 1d,
                fromOffsetY: 10d, toOffsetY: 0d,
                TimeSpan.FromMilliseconds(360));
        }
    }
}
