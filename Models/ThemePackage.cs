using System;
using System.Collections.Generic;

namespace VibrantbitLauncher.Models;

/// <summary>导航栏的摆放方式，由主题包决定（见 <see cref="ThemePackage.Navigation"/>）。</summary>
public enum ThemeNavigation
{
    /// <summary>左侧展开，图标 + 文字。</summary>
    Left,

    /// <summary>顶部横向。主窗口宽度不够时会自动收成图标。</summary>
    Top,

    /// <summary>
    /// 并进标题栏：导航项横向排在标题栏中间，标题栏本身就是导航条，
    /// 内容区上方不再单独占一行（PCL2 的蓝色顶栏就是这个结构）。
    /// </summary>
    Header,

    /// <summary>左侧只留图标条，悬停展开。本项目默认。</summary>
    Compact,

    /// <summary>完全收起，只留一个汉堡按钮。</summary>
    Minimal
}

/// <summary>
/// 一套主题包的元信息（来自包里的 <c>theme.json</c>）。
///
/// <para>
/// 主题包只管「长什么样」，不碰任何功能：一份 <c>theme.json</c> 描述元信息与导航栏模式，
/// 同目录的 <c>theme.axaml</c> / <c>light.axaml</c> / <c>dark.axaml</c> 覆盖资源键，
/// <c>styles.axaml</c> 覆盖控件外观。四份 XAML 都是可选的 —— 只写配色不写控件样式，
/// 或者反过来，都能单独成立。
/// </para>
/// </summary>
public sealed class ThemePackage
{
    /// <summary>主题 id，同时是它在磁盘上的目录名。只用小写字母、数字、连字符。</summary>
    public string Id { get; init; } = "default";

    /// <summary>界面里显示的名字。</summary>
    public string Name { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    /// <summary>一句介绍，显示在主题卡片下面。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>导航栏模式。未声明时用 <see cref="ThemeNavigation.Compact"/>（本项目原来的样子）。</summary>
    public ThemeNavigation Navigation { get; init; } = ThemeNavigation.Compact;

    /// <summary>
    /// 主题自带的强调色（<c>#RRGGBB</c> / <c>#AARRGGBB</c>）。为空则沿用用户在设置里选的主题色。
    /// 有值时整个强调色派生链（悬停、浅底、光晕）都会跟着它算。
    /// </summary>
    public string? Accent { get; init; }

    /// <summary>是不是随程序自带的那几套。内置主题不可删除。</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>包目录（内置主题为空 —— 它们的文件编译在程序集里）。</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>预览图路径（可选）。内置主题走资源 URI，用户主题走绝对路径。</summary>
    public string? PreviewPath { get; init; }

    /// <summary>能否在界面上删除（内置的不能）。</summary>
    public bool CanDelete => !IsBuiltIn;

    /// <summary>界面上的来源标签。</summary>
    public string SourceLabel => IsBuiltIn ? "内置" : "已安装";

    /// <summary>副标题：「作者 · v1.0」这类，缺项自动省略。</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(Author))
                parts.Add(Author);
            if (!string.IsNullOrWhiteSpace(Version))
                parts.Add("v" + Version.TrimStart('v', 'V'));
            return string.Join(" · ", parts);
        }
    }
}
