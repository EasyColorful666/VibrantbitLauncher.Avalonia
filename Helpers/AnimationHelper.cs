using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>
    /// 动效底层工具：对任意 <see cref="Visual"/> 做「透明度 + 缩放 + 纵向位移」的组合动画。
    ///
    /// 页面切换（<see cref="FluidPageTransition"/>）、页面内分栏切换
    /// （<see cref="PageTransitionHelper"/>）、窗口入场都共用这一份实现，
    /// 保证全应用的节奏（时长 / 缓动 / 位移量）一致。
    ///
    /// 实现要点：变换使用常驻的 <see cref="TransformGroup"/>{Scale, Translate}，
    /// 基准值直接写成**终态**，动画以 <see cref="FillMode.None"/> 运行 ——
    /// 播完自动回落到终态，既不需要额外的收尾赋值，也不会把元素「钉」在动画值上。
    /// </summary>
    internal static class AnimationHelper
    {
        /// <summary>默认缓动：先快后慢，观感干净不拖沓。</summary>
        public static Easing DefaultEasing { get; } = new CubicEaseOut();

        /// <summary>更「弹」一点的缓动，用于按钮这类需要即时反馈的小控件。</summary>
        public static Easing PlayfulEasing { get; } = new BackEaseOut();

        /// <summary>
        /// 播放一次组合动画（透明度 + 缩放 + 纵向位移同时进行）。
        /// </summary>
        public static Task AnimateAsync(
            Visual visual,
            double fromOpacity,
            double toOpacity,
            double fromScale,
            double toScale,
            double fromOffsetY,
            double toOffsetY,
            TimeSpan duration,
            Easing? easing = null,
            CancellationToken cancellationToken = default)
        {
            if (duration <= TimeSpan.Zero)
            {
                ApplyEndState(visual, toOpacity, toScale, toOffsetY);
                return Task.CompletedTask;
            }

            // 变换动画的宿主必须是 Visual：Avalonia 的 TransformAnimator 会去
            // visual.RenderTransform 里按类型找对应的 Scale / Translate 子变换，
            // 直接对 ScaleTransform 对象跑动画会抛 InvalidCastException。
            var (scale, translate) = EnsureTransform(visual);
            var ease = easing ?? DefaultEasing;

            // 基准值 = 终态
            visual.Opacity = toOpacity;
            scale.ScaleX = toScale;
            scale.ScaleY = toScale;
            translate.Y = toOffsetY;

            var tasks = new List<Task>(3);

            if (!AreEqual(fromOpacity, toOpacity))
            {
                tasks.Add(CreateAnimation(
                    duration, ease,
                    new (AvaloniaProperty, double)[] { (Visual.OpacityProperty, fromOpacity) },
                    new (AvaloniaProperty, double)[] { (Visual.OpacityProperty, toOpacity) })
                    .RunAsync(visual, cancellationToken));
            }

            if (!AreEqual(fromScale, toScale))
            {
                tasks.Add(CreateAnimation(
                    duration, ease,
                    new (AvaloniaProperty, double)[] { (ScaleTransform.ScaleXProperty, fromScale), (ScaleTransform.ScaleYProperty, fromScale) },
                    new (AvaloniaProperty, double)[] { (ScaleTransform.ScaleXProperty, toScale), (ScaleTransform.ScaleYProperty, toScale) })
                    .RunAsync(visual, cancellationToken));
            }

            if (!AreEqual(fromOffsetY, toOffsetY))
            {
                tasks.Add(CreateAnimation(
                    duration, ease,
                    new (AvaloniaProperty, double)[] { (TranslateTransform.YProperty, fromOffsetY) },
                    new (AvaloniaProperty, double)[] { (TranslateTransform.YProperty, toOffsetY) })
                    .RunAsync(visual, cancellationToken));
            }

            return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
        }

        /// <summary>跳过动画时直接把元素摆到终态。</summary>
        public static void ApplyEndState(Visual visual, double opacity, double scale, double offsetY)
        {
            var (scaleTransform, translateTransform) = EnsureTransform(visual);
            visual.Opacity = opacity;
            scaleTransform.ScaleX = scale;
            scaleTransform.ScaleY = scale;
            translateTransform.Y = offsetY;
        }

        /// <summary>
        /// 取得元素常驻的 Scale / Translate 变换；没有就建一个挂上去。
        /// 复用而不是每次新建，避免动画对象反复分配。
        /// </summary>
        private static (ScaleTransform Scale, TranslateTransform Translate) EnsureTransform(Visual visual)
        {
            if (visual.RenderTransform is TransformGroup group
                && group.Children.Count >= 2
                && group.Children[0] is ScaleTransform existingScale
                && group.Children[1] is TranslateTransform existingTranslate)
            {
                return (existingScale, existingTranslate);
            }

            var scale = new ScaleTransform(1, 1);
            var translate = new TranslateTransform(0, 0);
            visual.RenderTransform = new TransformGroup { Children = { scale, translate } };
            return (scale, translate);
        }

        /// <summary>组装一个两关键帧动画：起点一组 Setter，终点一组 Setter。</summary>
        private static Animation CreateAnimation(
            TimeSpan duration,
            Easing easing,
            (AvaloniaProperty Property, double Value)[] startSetters,
            (AvaloniaProperty Property, double Value)[] endSetters)
        {
            var start = new KeyFrame { Cue = new Cue(0d) };
            var end = new KeyFrame { Cue = new Cue(1d) };

            foreach (var (property, value) in startSetters)
                start.Setters.Add(new Setter(property, value));

            foreach (var (property, value) in endSetters)
                end.Setters.Add(new Setter(property, value));

            var animation = new Animation
            {
                Duration = duration,
                Easing = easing,
                FillMode = FillMode.None
            };

            animation.Children.Add(start);
            animation.Children.Add(end);
            return animation;
        }

        private static bool AreEqual(double a, double b) => Math.Abs(a - b) < 0.0001d;
    }
}
