using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Media.Transformation;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 给 <see cref="Visual.RenderTransform"/> 用的补丁动画器。
///
/// 为什么需要它：控件上 <c>RenderTransform</c> 的声明类型是接口 <see cref="ITransform"/>，
/// 而框架自带的动画器只在「属性类型正好是某个具体类型」时才命中（double、Thickness、
/// CornerRadius ……），接口类型永远匹配不到。于是任何把 <c>RenderTransform</c> 写进
/// <c>Style.Animations</c> 关键帧的样式，都会在挂样式的那一刻抛
/// 「No animator registered for the property RenderTransform」——整个窗口都起不来。
///
/// 关键帧里写的 <c>scale(1.04)</c> / <c>translateY(10px)</c> 解析出来是
/// <see cref="TransformOperations"/>（它实现 <see cref="ITransform"/>），所以这里把
/// 插值直接交给框架自带的 <see cref="TransformOperations.Interpolate"/>，
/// 插值曲线与官方实现完全一致，只是补上「按接口类型查找」这一步。
///
/// 注册见 <c>App.RegisterTransformAnimator</c>，必须早于任何窗口加载。
/// </summary>
public sealed class TransformOperationsAnimator : InterpolatingAnimator<ITransform>
{
    public override ITransform? Interpolate(double progress, ITransform? oldValue, ITransform? newValue)
    {
        // 元素原本没有 Transform（null）时，两个端点里取有值的那一头，
        // 不要按 50% 硬跳 —— 那会在动画中点闪一下。
        if (oldValue is null)
            return newValue;
        if (newValue is null)
            return oldValue;

        if (oldValue is TransformOperations from && newValue is TransformOperations to)
            return TransformOperations.Interpolate(from, to, progress);

        // 其它 ITransform 实现（ScaleTransform 等）不支持逐帧插值，退化成端点切换。
        return progress < 0.5 ? oldValue : newValue;
    }
}
