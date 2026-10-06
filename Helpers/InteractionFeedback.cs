using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>
    /// 点击反馈：按下时缩一下、松开时弹回。
    ///
    /// 为什么不用样式（Transitions + RenderTransform）而用代码跑动画：
    /// Avalonia 的属性优先级是
    ///   Animation(-1) &gt; LocalValue(0) &gt; StyleTrigger(1) &gt; 控件模板(2) &gt; 应用样式(3)
    /// FluentAvalonia 的导航项（FANavigationViewItem）在控件模板里就把
    /// <c>RenderTransform</c> / <c>Transitions</c> 写成了 null，应用级样式永远压不过它，
    /// 实测按下去的缩放完全无效（见 1.0.5.12 验证记录）。
    /// 走 Animation 优先级 + 给 RenderTransform 写局部值，才是唯一稳的做法。
    ///
    /// 普通 Button 由 Styles/Animations.axaml 里的样式负责，不需要挂这个。
    /// </summary>
    public static class InteractionFeedback
    {
        private static readonly TimeSpan PressDuration = TimeSpan.FromMilliseconds(90);
        private static readonly TimeSpan ReleaseDuration = TimeSpan.FromMilliseconds(240);

        /// <summary>
        /// 给 <paramref name="control"/> 挂上点击反馈。
        /// </summary>
        /// <param name="control">目标控件（FluentAvalonia 的导航项等）。</param>
        /// <param name="pressedScale">按下时的缩放，默认 0.97。</param>
        public static void AttachPressFeedback(Control control, double pressedScale = 0.97)
        {
            var pressed = false;

            void OnPressed(object? sender, PointerPressedEventArgs e)
            {
                if (!control.IsEnabled)
                    return;

                if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
                    return;

                pressed = true;
                _ = AnimationHelper.AnimateScaleAsync(
                    control, 1d, pressedScale, PressDuration, AnimationHelper.DefaultEasing);
            }

            void OnReleased(object? sender, PointerReleasedEventArgs e)
            {
                if (!pressed)
                    return;

                pressed = false;
                _ = AnimationHelper.AnimateScaleAsync(
                    control, pressedScale, 1d, ReleaseDuration, AnimationHelper.PlayfulEasing);
            }

            // 指针被别的控件夺走（例如按下后拖出控件）也要复位，否则会一直缩着
            void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
            {
                if (!pressed)
                    return;

                pressed = false;
                _ = AnimationHelper.AnimateScaleAsync(
                    control, pressedScale, 1d, ReleaseDuration, AnimationHelper.PlayfulEasing);
            }

            // 用 Tunnel 而不是 Bubble：子元素（图标 / 文本）不该有机会先把事件吃掉
            control.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            control.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
            control.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
        }
    }
}
