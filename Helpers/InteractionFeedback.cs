using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>
    /// 点击反馈：按下时缩一下（1.5%）、松开时弹回。
    ///
    /// 为什么不用样式（Transitions + RenderTransform）而用代码跑动画：
    /// Avalonia 的属性优先级是
    ///   Animation(-1) &gt; LocalValue(0) &gt; StyleTrigger(1) &gt; 控件模板(2) &gt; 应用样式(3)
    /// FluentAvalonia 的导航项（FANavigationViewItem）在控件模板里就把
    /// <c>RenderTransform</c> / <c>Transitions</c> 写成了 null，应用级样式永远压不过它，
    /// 实测按下去的缩放完全无效（见 1.0.5.12 验证记录）。
    /// 走 Animation 优先级 + 给 RenderTransform 写局部值，才是唯一稳的做法。
    ///
    /// 普通 Button / ListBoxItem 由 Styles/Animations.axaml 里的样式负责，不需要挂这个。
    ///
    /// 节奏（1.0.29.0）：按下 100ms、松开 150ms —— 都在「反馈」这一档里，
    /// 且松开略长，因为回弹是"恢复"不是"响应"。精简 / 关闭档不挂。
    /// </summary>
    public static class InteractionFeedback
    {
        private static readonly TimeSpan PressDuration = TimeSpan.FromMilliseconds(MotionSettings.FeedbackMs);
        private static readonly TimeSpan ReleaseDuration = TimeSpan.FromMilliseconds(150);

        /// <summary>按住时的缩放。1.5% 刚好能感到"按到了"，再大就像果冻。</summary>
        private const double DefaultPressedScale = 0.985d;

        /// <summary>
        /// 给 <paramref name="control"/> 挂上点击反馈。
        /// </summary>
        /// <param name="control">目标控件（FluentAvalonia 的导航项等）。</param>
        /// <param name="pressedScale">按下时的缩放，默认 0.985。</param>
        public static void AttachPressFeedback(Control control, double pressedScale = DefaultPressedScale)
        {
            var pressed = false;

            void OnPressed(object? sender, PointerPressedEventArgs e)
            {
                if (!control.IsEnabled)
                    return;

                if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
                    return;

                // 关闭 / 精简档：回缩属于"装饰性位移"，不播。颜色反馈仍然由样式给。
                // 判断放在事件里而不是挂载时 —— 用户在设置里改档位后不必重启就生效。
                if (!MotionSettings.AllowMovement)
                    return;

                pressed = true;
                _ = AnimationHelper.AnimateScaleAsync(
                    control, 1d, pressedScale, PressDuration, MotionSettings.Feedback);
            }

            void OnReleased(object? sender, PointerReleasedEventArgs e)
            {
                if (!pressed)
                    return;

                pressed = false;
                _ = AnimationHelper.AnimateScaleAsync(
                    control, pressedScale, 1d, ReleaseDuration, MotionSettings.Enter);
            }

            // 指针被别的控件夺走（例如按下后拖出控件）也要复位，否则会一直缩着
            void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
            {
                if (!pressed)
                    return;

                pressed = false;
                _ = AnimationHelper.AnimateScaleAsync(
                    control, pressedScale, 1d, ReleaseDuration, MotionSettings.Enter);
            }

            // 用 Tunnel 而不是 Bubble：子元素（图标 / 文本）不该有机会先把事件吃掉
            control.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            control.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
            control.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
        }
    }
}
