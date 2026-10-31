using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 按屏幕分辨率给窗口定尺寸。
///
/// <para>
/// <b>为什么要这个</b>：本工程的主窗口是按 1180×740 设计的，而目标用户里有
/// 1366×768 的笔记本、1280×720 的老台式机。硬写 1180×740 的后果是窗口
/// 底部（正好是启动按钮那一排）被任务栏压掉，用户以为「按钮没了」。
/// </para>
///
/// <para>
/// <b>单位</b>：Avalonia 的 <c>Width</c> / <c>Height</c> 是 DIP，而
/// <see cref="Screen.WorkingArea"/> 给的是物理像素 —— 必须除以
/// <see cref="Screen.Scaling"/> 才能比较。125% 缩放的笔记本上忘了除，
/// 算出来的窗口会大出四分之一，正好又是「出界」。
/// </para>
///
/// <para>
/// 用 <c>WorkingArea</c> 而不是 <c>Bounds</c>：后者包含任务栏，
/// 拿它算出来的高度会让窗口最后一行被任务栏盖住。
/// </para>
/// </summary>
public static class WindowSizing
{
    /// <summary>
    /// 窗口最多占工作区的比例。留一点边，既不会顶满屏幕，也保证标题栏不贴边。
    /// </summary>
    private const double WorkingAreaRatio = 0.94d;

    /// <summary>
    /// 按屏幕工作区把窗口收进可见范围，并把结果写回窗口。
    ///
    /// <para>
    /// 规则：取「设计尺寸」与「工作区 94%」的较小值；如果屏幕小到连设计最小尺寸
    /// 都放不下，就把最小尺寸一起下调到工作区大小（否则窗口必然出界，
    /// 而 <c>MinWidth</c> 大于屏幕宽度时用户既拖不动也看不全）。
    /// </para>
    /// </summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="desiredWidth">设计宽度（DIP）。</param>
    /// <param name="desiredHeight">设计高度（DIP）。</param>
    /// <param name="minWidth">设计最小宽度（DIP）。</param>
    /// <param name="minHeight">设计最小高度（DIP）。</param>
    /// <returns>是否成功取到屏幕信息（false 表示没动窗口，值保持原样）。</returns>
    public static bool FitToScreen(
        Window window,
        double desiredWidth,
        double desiredHeight,
        double minWidth,
        double minHeight)
    {
        if (window is null)
            return false;

        var (availableWidth, availableHeight) = GetWorkingAreaDip(window);
        if (availableWidth <= 0d || availableHeight <= 0d)
            return false;

        var maxWidth = availableWidth * WorkingAreaRatio;
        var maxHeight = availableHeight * WorkingAreaRatio;

        // 最小尺寸不能大于可用空间，否则窗口一定出界
        var effectiveMinWidth = Math.Min(minWidth, maxWidth);
        var effectiveMinHeight = Math.Min(minHeight, maxHeight);

        var width = Math.Clamp(desiredWidth, effectiveMinWidth, maxWidth);
        var height = Math.Clamp(desiredHeight, effectiveMinHeight, maxHeight);

        window.MinWidth = Math.Round(effectiveMinWidth);
        window.MinHeight = Math.Round(effectiveMinHeight);
        window.Width = Math.Round(width);
        window.Height = Math.Round(height);

        Serilog.Log.Debug(
            "窗口尺寸适配：屏幕可用 {AvailW:F0}×{AvailH:F0} DIP → {Type} {W:F0}×{H:F0}（设计 {DesW:F0}×{DesH:F0}）",
            availableWidth, availableHeight, window.GetType().Name, width, height,
            desiredWidth, desiredHeight);

        return true;
    }

    /// <summary>
    /// 取主屏工作区的 DIP 尺寸（已除掉任务栏、已按 DPI 换算）。
    /// 拿不到屏幕信息时返回 (0, 0)，由调用方决定回退。
    /// </summary>
    public static (double Width, double Height) GetWorkingAreaDip(Window window)
    {
        try
        {
            var screens = window.Screens;
            if (screens is null)
                return (0d, 0d);

            var screen = screens.Primary ?? screens.All.FirstOrDefault();
            if (screen is null)
                return (0d, 0d);

            var area = screen.WorkingArea;
            var scaling = screen.Scaling;

            // Scaling 理论上恒 > 0，但驱动异常时见过 0，除零会让窗口直接消失
            if (scaling <= 0d)
                scaling = 1d;

            return (area.Width / scaling, area.Height / scaling);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取屏幕工作区失败，窗口尺寸保持设计值");
            return (0d, 0d);
        }
    }

    /// <summary>
    /// 把浮层（内嵌子窗口卡片）的最大尺寸限制在宿主可视区域内。
    ///
    /// <para>
    /// 内嵌子窗口原来是独立 <c>Window</c>，各自带着固定的 <c>Width/Height</c>
    /// （440×290、900×620…）。浮到主窗口里之后，遇到小屏 + 主窗口被压缩，
    /// 固定的高度会顶到宿主外面去。这里给它一个上限，超出部分由外层
    /// <c>ScrollViewer</c> 滚动 —— 挤压布局比让用户看不到底部按钮要好。
    /// </para>
    /// </summary>
    /// <param name="floatingCard">浮层卡片。</param>
    /// <param name="hostWidth">宿主可视宽度（DIP）。</param>
    /// <param name="hostHeight">宿主可视高度（DIP）。</param>
    /// <param name="margin">四周留白（与 XAML 里 ScrollViewer 的 Margin 保持一致）。</param>
    public static void ConstrainToHost(
        Control? floatingCard,
        double hostWidth,
        double hostHeight,
        double margin = 32d)
    {
        if (floatingCard is null)
            return;

        // 宿主尚未布局（第一次打开时 Bounds 还是 0）：不设上限，等下一次打开再收
        if (hostWidth <= 0d || hostHeight <= 0d)
            return;

        // 下限给一点余量，别把卡片压成一条缝（真到那一步应该改布局，而不是继续挤）
        floatingCard.MaxWidth = Math.Max(320d, hostWidth - margin);
        floatingCard.MaxHeight = Math.Max(240d, hostHeight - margin);
    }
}
