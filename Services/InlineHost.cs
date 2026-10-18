using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 内嵌子页宿主（学 PCL2 的「子窗口 = 换个页面」）。
///
/// <para>
/// <b>为什么不再弹窗</b>：这个启动器的子窗口有十来个（登录、换肤、补资源、崩溃分析、
/// 实例工具箱、配装码、联机模块…），每个都是独立 <c>Window</c> —— 会跑到任务栏、
/// 会被 Alt+Tab 当成另一个程序、在 Windows 上还要自己处理标题栏与主题，而且
/// 主窗口缩小后子窗口容易飞到屏幕外。PCL2 的做法是把它们作为主窗口内容区的一个页面，
/// 顶部给一个返回按钮。这里照做。
/// </para>
///
/// <para>
/// <b>用法</b>：原来 <c>new XxxWindow().ShowDialog(owner)</c> 的地方改成
/// <c>await InlineHost.ShowAsync(new XxxWindow(), "标题")</c>；
/// 子页内部原来调 <c>Close()</c> 的地方改成 <see cref="Close"/>。
/// </para>
///
/// <para>
/// 同一时刻只允许一个内嵌页（与「模态子窗口」的语义一致）。真的叠两层时
/// 新的会把旧的顶掉 —— 比让两个页面同时抢主窗口安全。
/// </para>
/// </summary>
public interface IInlinePage
{
    /// <summary>宿主顶栏上显示的标题。</summary>
    string InlineTitle { get; }
}

public static class InlineHost
{
    private static TaskCompletionSource<bool>? _current;

    /// <summary>当前内嵌页控件（供 <see cref="SetTitle"/> 改标题）。</summary>
    private static Control? _currentContent;

    /// <summary>当前是否有内嵌子页在显示。</summary>
    public static bool IsOpen => _current is not null;

    /// <summary>
    /// 在主窗口内容区显示一个子页，直到它调用 <see cref="Close"/>。
    /// </summary>
    /// <param name="content">子页控件（原来的 Window 改成 UserControl 后直接传进来）。</param>
    /// <param name="title">顶部标题；为空时用控件类型名去掉 Window 后缀。</param>
    public static async Task ShowAsync(Control content, string? title = null)
    {
        if (content is null)
            return;

        var main = Views.Windows.MainWindow.Instance;
        if (main is null)
        {
            // 主窗口还没建好（极早期调用）：退回成独立窗口，至少不会丢功能。
            Serilog.Log.Warning("主窗口尚未就绪，内嵌页改用独立窗口显示：{Type}", content.GetType().Name);
            if (content is Window window)
                window.Show();
            return;
        }

        // 已有内嵌页：先把旧的收掉，避免叠两层互相抢焦点
        _current?.TrySetResult(true);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _current = tcs;

        _currentContent = content;
        main.ShowInline(content);

        try
        {
            await tcs.Task.ConfigureAwait(true);
        }
        finally
        {
            _currentContent = null;
            main.HideInline();

            // 只清自己那一份：被别人顶掉时 _current 已经换人了，不能误清新页的令牌
            if (ReferenceEquals(_current, tcs))
                _current = null;
        }
    }

    /// <summary>关闭当前内嵌页（子页内部的「确定 / 取消 / 完成」都调它）。</summary>
    public static void Close()
    {
        _current?.TrySetResult(true);
    }

    /// <summary>
    /// 子页运行中想改标题（例如皮肤页带上账户名）时调用。
    ///
    /// 内嵌页用的是它自己那条 <see cref="Controls.TitleBar"/>（原窗口的标题栏），
    /// 所以这里改的是页内标题栏，而不是宿主顶栏 —— 宿主根本没有顶栏。
    /// </summary>
    public static void SetTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || _currentContent is null)
            return;

        if (Avalonia.VisualTree.VisualExtensions.FindDescendantOfType<Controls.TitleBar>(_currentContent)
            is { } bar)
        {
            bar.Title = title;
        }
    }

    private static string DefaultTitle(Control content)
    {
        var name = content.GetType().Name;
        return name.EndsWith("Window", StringComparison.Ordinal)
            ? name[..^"Window".Length]
            : name;
    }
}
