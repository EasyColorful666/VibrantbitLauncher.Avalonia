using System;
using Avalonia;
using Avalonia.Controls;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>
    /// 轻量入场动效的统一入口。
    ///
    /// 下载中心（左侧分类）、设置页（左侧分类）的分栏切换，以及主窗口首次显示，
    /// 都走这里，页面不必各写一份动画代码；节奏（时长 / 缓动 / 位移量）全应用一致。
    ///
    /// <para>
    /// 1.0.29.0 起支持<b>方向</b>：切换分类时如果新分类在旧分类右边，
    /// 内容就从右侧 10px 处落定，在左边则从左落定 —— 位移很小，但方向对得上，
    /// 用户能感到"我往右走了一格"。首屏 / 无方向时只做上浮。
    /// </para>
    /// </summary>
    public static class PageTransitionHelper
    {
        /// <summary>横向位移量（px）。比纵向的 12px 更小，因为横向更容易被察觉。</summary>
        private const double SlideOffsetX = 10d;

        /// <summary>纵向位移量（px）。</summary>
        private const double RiseOffsetY = 10d;

        /// <summary>
        /// 让 <paramref name="element"/> 播一次入场动画。
        /// </summary>
        /// <param name="element">要入场的内容区。</param>
        /// <param name="direction">
        /// 切换方向：1 = 新内容从右侧落定，-1 = 从左侧落定，0 = 只上浮（首屏、未知方向）。
        /// </param>
        public static void PlayEnterTransition(Visual? element, int direction = 0)
        {
            if (element is not Control control)
                return;

            var mode = MotionSettings.PageTransition;

            // 关闭动效：直接落到终态。不播动画也必须归位，否则上一次动画没跑完就切走，
            // 元素会停在「半透明 + 偏移」的中间态。
            if (mode == "None")
            {
                AnimationHelper.ApplyEndState(control, 1d, 1d, 0d);
                AnimationHelper.PrepareSlideX(control, 1d, 0d);
                return;
            }

            // 平移档被关掉、或用户在「精简」档：只保留透明度，
            // 两者要表达的都是「换了一屏」，位移只是锦上添花。
            var move = mode == "Slide" && MotionSettings.AllowMovement;

            var duration = MotionSettings.Ms(MotionSettings.EnterMs);

            if (!move)
            {
                _ = AnimationHelper.AnimateAsync(
                    control,
                    fromOpacity: 0d, toOpacity: 1d,
                    fromScale: 1d, toScale: 1d,
                    fromOffsetY: 0d, toOffsetY: 0d,
                    duration,
                    MotionSettings.Enter);
                return;
            }

            // 先摆起点：AnimateAsync / AnimateSlideXAsync 只写自己要动的属性，
            // 两个方向同时存在时必须自己把另一轴归零，否则上一轮残留会叠上来。
            var startX = direction > 0 ? SlideOffsetX : direction < 0 ? -SlideOffsetX : 0d;

            AnimationHelper.PrepareSlideX(control, 0d, startX);

            // ⚠️ 透明度只交给一个动画：两条动画同时写 Opacity 会互相拽，
            // 观感是忽明忽暗。所以 X 轴那条负责「淡入 + 横移」，
            // Y 轴那条只写 RenderTransform 的纵向分量（起止透明度相同 → 自动跳过）。
            _ = AnimationHelper.AnimateSlideXAsync(
                control,
                fromOpacity: 0d, toOpacity: 1d,
                fromOffsetX: startX, toOffsetX: 0d,
                duration,
                MotionSettings.Enter);

            _ = AnimationHelper.AnimateAsync(
                control,
                fromOpacity: 1d, toOpacity: 1d,
                fromScale: 1d, toScale: 1d,
                fromOffsetY: RiseOffsetY, toOffsetY: 0d,
                duration,
                MotionSettings.Enter);
        }

        /// <summary>
        /// 主窗口内容入场：整体淡入 + 从 98.5% 推到 100%。
        ///
        /// 窗口入场比页面切换更缓（300ms），避免「窗口和页面同时动」显得吵。
        /// </summary>
        public static void PlayWindowEntrance(Visual? element)
        {
            if (element is not Control control)
                return;

            if (!MotionSettings.Enabled)
            {
                AnimationHelper.ApplyEndState(control, 1d, 1d, 0d);
                return;
            }

            _ = AnimationHelper.AnimateAsync(
                control,
                fromOpacity: 0d, toOpacity: 1d,
                fromScale: MotionSettings.AllowMovement ? 0.985d : 1d, toScale: 1d,
                fromOffsetY: MotionSettings.AllowMovement ? 10d : 0d, toOffsetY: 0d,
                MotionSettings.Ms(MotionSettings.WindowEnterMs),
                MotionSettings.Enter);
        }
    }
}
