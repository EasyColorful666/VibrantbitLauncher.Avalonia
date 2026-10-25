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

            var mode = MotionSettings.PageTransition;

            // 关闭动效：直接落到终态。不播动画也必须归位，否则上一次动画没跑完就切走，
            // 元素会停在「半透明 + 偏移」的中间态。
            if (mode == "None")
            {
                AnimationHelper.ApplyEndState(control, 1d, 1d, 0d);
                return;
            }

            // 「渐变」与「精简」都只做透明度：两者要表达的都是「换了一屏」，
            // 位移只是锦上添花，去掉之后反而更快更稳。
            if (mode == "Fade" || MotionSettings.Reduced)
            {
                _ = AnimationHelper.AnimateAsync(
                    control,
                    fromOpacity: 0d, toOpacity: 1d,
                    fromScale: 1d, toScale: 1d,
                    fromOffsetY: 0d, toOffsetY: 0d,
                    MotionSettings.Scale(TimeSpan.FromMilliseconds(180)));
                return;
            }

            _ = AnimationHelper.AnimateAsync(
                control,
                fromOpacity: 0d, toOpacity: 1d,
                fromScale: 0.985d, toScale: 1d,
                fromOffsetY: 18d, toOffsetY: 0d,
                MotionSettings.Scale(TimeSpan.FromMilliseconds(260)));
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

            if (!MotionSettings.Enabled || MotionSettings.Reduced)
            {
                AnimationHelper.ApplyEndState(control, 1d, 1d, 0d);
                return;
            }

            _ = AnimationHelper.AnimateAsync(
                control,
                fromOpacity: 0d, toOpacity: 1d,
                fromScale: 0.985d, toScale: 1d,
                fromOffsetY: 10d, toOffsetY: 0d,
                MotionSettings.Scale(TimeSpan.FromMilliseconds(360)));
        }
    }
}
