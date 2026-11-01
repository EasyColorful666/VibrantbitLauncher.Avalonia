using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>
    /// 页面切换过渡：旧页淡出并略缩小（快走），新页上浮 + 淡入（落定）。
    ///
    /// 为什么不用 Avalonia 自带的 <c>PageSlide</c> / <c>CrossFade</c>：
    ///   · PageSlide 是左右平移，侧边栏切页并没有「前进 / 后退」的方向语义，
    ///     横向滑动会让人以为可以左右返回；
    ///   · CrossFade 只有透明度，两页在交叉的那一帧会糊成一片。
    /// 这里做成「淡出收缩 + 淡入上浮」的层次切换，任何两页之间都说得通，
    /// 也是 WinUI 系应用里最常见的过渡观感。
    ///
    /// 节奏（1.0.29.0 收敛到全站时序表，见 <see cref="MotionSettings"/>）：
    ///   进场 250ms / EaseEnter（先快后慢）、出场 150ms / EaseExit（先慢后快）。
    ///   出场比进场短，是为了让新内容早点开始"落定"。
    /// </summary>
    public sealed class FluidPageTransition : IPageTransition
    {
        /// <summary>新页起始缩放（略小于 1，浮起来时有「推近」的感觉）。</summary>
        public double InStartScale { get; set; } = 0.988;

        /// <summary>新页起始下移量（px）。</summary>
        public double InStartOffsetY { get; set; } = 12;

        /// <summary>旧页结束缩放（略小于 1，做出「退到后面」的层次）。</summary>
        public double OutEndScale { get; set; } = 0.994;

        /// <summary>旧页结束上移量（px）。</summary>
        public double OutEndOffsetY { get; set; } = -6;

        public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
        {
            // 某些实现会复用同一个 presenter，自取自演会互相打架
            if (from is not null && ReferenceEquals(from, to))
                from = null;

            // 关闭动效：直接把两页摆到终态，一帧都不画
            if (!MotionSettings.Enabled)
            {
                if (from is not null)
                    AnimationHelper.ApplyEndState(from, 1d, 1d, 0d);
                if (to is not null)
                    AnimationHelper.ApplyEndState(to, 1d, 1d, 0d);

                return Task.CompletedTask;
            }

            // 精简档：只做透明度，位移与缩放全部取消
            var move = MotionSettings.AllowMovement;

            var outDuration = MotionSettings.Ms(MotionSettings.ExitMs);
            var inDuration = MotionSettings.Ms(MotionSettings.EnterMs);

            var outTask = from is null
                ? Task.CompletedTask
                : AnimationHelper.AnimateAsync(
                    from,
                    fromOpacity: 1d, toOpacity: 0d,
                    fromScale: 1d, toScale: move ? OutEndScale : 1d,
                    fromOffsetY: 0d, toOffsetY: move ? OutEndOffsetY : 0d,
                    outDuration,
                    MotionSettings.Exit,
                    cancellationToken);

            var inTask = to is null
                ? Task.CompletedTask
                : AnimationHelper.AnimateAsync(
                    to,
                    fromOpacity: 0d, toOpacity: 1d,
                    fromScale: move ? InStartScale : 1d, toScale: 1d,
                    fromOffsetY: move ? InStartOffsetY : 0d, toOffsetY: 0d,
                    inDuration,
                    MotionSettings.Enter,
                    cancellationToken);

            return Task.WhenAll(outTask, inTask);
        }
    }
}
