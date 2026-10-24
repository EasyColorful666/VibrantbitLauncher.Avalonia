using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Controls;

/// <summary>
/// 内嵌子页的浮层宿主（一层遮罩 + 居中卡片 + 内容槽）。
///
/// <para>
/// 主窗口与首次使用向导各挂一个。浮层跟随「哪个窗口在显示」而走 —— 向导期间主窗口是隐藏的，
/// 若仍然只往主窗口里画，用户点了按钮什么都看不到（表现为「点击没反应」）。
/// </para>
///
/// <para>
/// 一次只承载一个子页。切页时旧页先飞完再开新页，两段动画不会同时改同一组变换。
/// </para>
/// </summary>
public partial class InlineHostOverlay : UserControl
{
    /// <summary>当前正在承载的子页；为空表示浮层收起。</summary>
    public Control? HostedContent => ContentHost.Content as Control;

    /// <summary>当前是否有一个子页显示在其中。</summary>
    public bool IsHosting => ContentHost.Content is not null;

    private CancellationTokenSource? _motionCts;

    public InlineHostOverlay()
    {
        InitializeComponent();

        // 窗口尺寸变化（小屏、拉伸、换显示器）时重新收一次卡片上限
        SizeChanged += (_, _) => ConstrainCard();
    }

    /// <summary>写入内容并飞入。返回时浮层已经可见，控制权交给子页。</summary>
    public async Task ShowAsync(Control content)
    {
        _motionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _motionCts = cts;

        ContentHost.Content = content;
        IsVisible = true;

        ConstrainCard();

        // 子页出现时把焦点交给它：否则键盘焦点还停在被盖住的控件上，
        // Tab / 方向键会去操作看不见的东西。
        content.Focus();

        try
        {
            await WindowMotion.FlyInAsync(Card, Scrim, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 被下一个子页顶掉：新页已经接管同一组元素，这里什么都不用做
        }
        finally
        {
            if (ReferenceEquals(_motionCts, cts))
                _motionCts = null;

            cts.Dispose();
        }
    }

    /// <summary>
    /// 先播飞出，播完才真正隐藏并卸掉内容。
    /// <paramref name="content"/> 用来判断「飞出期间是不是已经有新页接管了」——
    /// 是的话只让它飞完，别把新页一起收掉。
    /// </summary>
    public async Task HideAsync(Control? content)
    {
        // 宿主窗口本身不可见（正在关闭）：不必再飞一次，直接收掉
        if (!IsVisible)
        {
            ContentHost.Content = null;
            return;
        }

        _motionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _motionCts = cts;

        try
        {
            await WindowMotion.FlyOutAsync(Card, Scrim, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 飞出中途被新页顶掉：保留宿主可见，交给新页自己飞入
            return;
        }
        finally
        {
            if (ReferenceEquals(_motionCts, cts))
                _motionCts = null;

            cts.Dispose();
        }

        if (content is not null && !ReferenceEquals(ContentHost.Content, content))
            return;

        IsVisible = false;
        ContentHost.Content = null;
    }

    /// <summary>把卡片的最大尺寸限制在宿主实际可视区内。</summary>
    private void ConstrainCard()
        => WindowSizing.ConstrainToHost(Card, Bounds.Width, Bounds.Height);
}
