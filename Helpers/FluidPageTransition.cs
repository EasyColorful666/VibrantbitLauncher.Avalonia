using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>
    /// 页面切换过渡：旧页轻微淡出并略缩小，新页从下方浮起 + 淡入。
    ///
    /// 为什么不用 Avalonia 自带的 <c>PageSlide</c> / <c>CrossFade</c>：
    ///   · PageSlide 是左右平移，侧边栏切页并没有「前进 / 后退」的方向语义，
    ///     横向滑动会让人以为可以左右返回；
    ///   · CrossFade 只有透明度，两页在交叉的那一帧会糊成一片。
    /// 这里做成「淡出收缩 + 淡入上浮」的层次切换，任何两页之间都说得通，
    /// 也是 WinUI 系应用里最常见的过渡观感。
    /// </summary>
    public sealed class FluidPageTransition : IPageTransition
    {
        /// <summary>入场时长。</summary>
        public TimeSpan InDuration { get; set; } = TimeSpan.FromMilliseconds(300);

        /// <summary>出场时长（比入场短，避免两页在屏幕上叠太久）。</summary>
        public TimeSpan OutDuration { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>新页起始缩放（略小于 1，浮起来时有「推近」的感觉）。</summary>
        public double InStartScale { get; set; } = 0.982;

        /// <summary>新页起始下移量（px）。</summary>
        public double InStartOffsetY { get; set; } = 16;

        /// <summary>旧页结束缩放（略小于 1，做出「退到后面」的层次）。</summary>
        public double OutEndScale { get; set; } = 0.988;

        /// <summary>旧页结束上移量（px）。</summary>
        public double OutEndOffsetY { get; set; } = -8;

        public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
        {
            // 某些实现会复用同一个 presenter，自取自演会互相打架
            if (from is not null && ReferenceEquals(from, to))
                from = null;

            var outTask = from is null
                ? Task.CompletedTask
                : AnimationHelper.AnimateAsync(
                    from,
                    fromOpacity: 1d, toOpacity: 0d,
                    fromScale: 1d, toScale: OutEndScale,
                    fromOffsetY: 0d, toOffsetY: OutEndOffsetY,
                    OutDuration,
                    AnimationHelper.DefaultEasing,
                    cancellationToken);

            var inTask = to is null
                ? Task.CompletedTask
                : AnimationHelper.AnimateAsync(
                    to,
                    fromOpacity: 0d, toOpacity: 1d,
                    fromScale: InStartScale, toScale: 1d,
                    fromOffsetY: InStartOffsetY, toOffsetY: 0d,
                    InDuration,
                    AnimationHelper.DefaultEasing,
                    cancellationToken);

            return Task.WhenAll(outTask, inTask);
        }
    }
}
