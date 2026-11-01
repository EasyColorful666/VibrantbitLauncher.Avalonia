using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 列表的「逐项落定」入场动效。
///
/// <para>
/// <b>用法</b>：在样式里给所有 <see cref="ListBox"/> 打开这个附加属性即可
/// （见 <c>Styles/Animations.axaml</c> 第 7 节），页面不需要写任何代码。
/// 需要给 <see cref="ItemsControl"/>（图墙、栅格）也加时，给它挂上 <c>Classes="stagger"</c>。
/// </para>
///
/// <para>
/// <b>为什么按「项」而不是整个列表播</b>：整块一起动看起来像页面切换；
/// 逐项错开 20ms 才有「列表一条条列出来」的观感，也和页面切换的节奏区分开。
/// </para>
///
/// <para>
/// <b>为什么要延迟两帧再取容器</b>：ListBox 的项容器是布局阶段才生成的，
/// <c>AttachedToVisualTree</c> 当下 <c>GetRealizedContainers()</c> 往往还是空的 ——
/// 直接播会退化成「整块飞入」。
/// </para>
///
/// <para>
/// <b>节奏（1.0.29.0）</b>：单项 250ms / EaseEnter，相邻间隔 20ms，最多错开 12 项；
/// 精简档退化为纯淡入，关闭档一帧都不画。位移量 24px 比页面切换还小 ——
/// 一屏几十行如果都滑 30px 以上，会像在「刷牌」。
/// </para>
/// </summary>
public static class ListMotion
{
    /// <summary>飞入的起始横向偏移（px，负值 = 从左侧进来）。</summary>
    private const double StartOffsetX = 24d;

    /// <summary>
    /// 参与错开的最大项数。再往后的一律直接显示 ——
    /// 几百个项的列表如果按 20ms 累加，最后一项要等好几秒才出现。
    /// </summary>
    private const int MaxStaggerItems = 12;

    /// <summary>
    /// 打开后，该 <see cref="ItemsControl"/> 每次挂到可视树上都会播一次「逐项落定」。
    /// 用附加属性是为了能从样式批量套到所有列表上，不必逐个页面改 XAML。
    /// </summary>
    public static readonly AttachedProperty<bool> EnableStaggeredFlyInProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "EnableStaggeredFlyIn", typeof(ListMotion));

    public static bool GetEnableStaggeredFlyIn(Control control)
        => control.GetValue(EnableStaggeredFlyInProperty);

    public static void SetEnableStaggeredFlyIn(Control control, bool value)
        => control.SetValue(EnableStaggeredFlyInProperty, value);

    static ListMotion()
    {
        EnableStaggeredFlyInProperty.Changed.AddClassHandler<Control>(OnPropertyChanged);
    }

    private static void OnPropertyChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        if (control is not ItemsControl items)
            return;

        // 先摘再挂：样式重复求值时不至于订阅多份，播两次动画
        items.AttachedToVisualTree -= OnAttachedToVisualTree;

        if (args.GetNewValue<bool>())
            items.AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private static void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is ItemsControl items)
            _ = PlayAsync(items);
    }

    private static async Task PlayAsync(ItemsControl items)
    {
        // 关闭档：一帧都不画（统一入口见 MotionSettings）
        if (!MotionSettings.Enabled)
            return;

        try
        {
            // 等布局把项容器生成出来（Loaded：布局已完成；再让一轮背景任务兜底虚拟化）
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

            var containers = items.GetRealizedContainers()
                .OfType<Control>()
                .OrderBy(c => c.Bounds.Y)
                .ThenBy(c => c.Bounds.X)
                .ToList();

            // 容器还没生成（数据是异步填的 / 空列表）：退化成整个列表淡入，
            // 至少还有动效，不至于「什么都没有」。
            if (containers.Count == 0)
            {
                await AnimateItemAsync(items, 0);
                return;
            }

            // 只对前 N 项错开，其余保持原样（它们多半在滚动视口之外）
            var staggerCount = Math.Min(containers.Count, MaxStaggerItems);
            for (var i = 0; i < containers.Count; i++)
            {
                if (i >= staggerCount)
                    break;

                _ = AnimateItemAsync(containers[i], i * MotionSettings.StaggerMs);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "列表框入场动画失败（不影响显示）");
        }
    }

    private static async Task AnimateItemAsync(Control item, double delayMs)
    {
        // 精简档：只淡入，不做横向位移
        var offsetFrom = MotionSettings.AllowMovement ? StartOffsetX : 0d;

        // 先摆到起点：否则用户会先看到完整内容，再跳回左侧重播
        AnimationHelper.PrepareSlideX(item, 0d, offsetFrom);

        if (delayMs > 0)
        {
            try
            {
                await Task.Delay((int)delayMs);
            }
            catch
            {
                return;
            }
        }

        try
        {
            await AnimationHelper.AnimateSlideXAsync(
                item, 0d, 1d, offsetFrom, 0d,
                MotionSettings.Ms(MotionSettings.EnterMs),
                MotionSettings.Enter);
        }
        catch (OperationCanceledException)
        {
            // 页面切走导致动画取消：元素已经不可见，无需补救
        }
        catch (Exception ex)
        {
            // 单项失败不能让整页留白：直接把元素摆回终态
            AnimationHelper.PrepareSlideX(item, 1d, 0d);
            Serilog.Log.Debug(ex, "列表单项入场动画失败");
        }
        finally
        {
            ClearMotionValues(item);
        }
    }

    /// <summary>
    /// 清理动画留下的局部值，把 <c>RenderTransform</c> / <c>Opacity</c> 交还给样式。
    ///
    /// <para>
    /// 必须清：<c>ListBoxItem</c> 的「按住回缩」是样式里的
    /// <c>RenderTransform = scale(0.99)</c>，而代码设置的局部值优先级高于样式 ——
    /// 不清的话，播完动画后所有列表项的点击回缩就永久失效了。
    /// </para>
    /// </summary>
    private static void ClearMotionValues(Control control)
    {
        control.ClearValue(Visual.RenderTransformProperty);
        control.ClearValue(Visual.OpacityProperty);
    }
}
