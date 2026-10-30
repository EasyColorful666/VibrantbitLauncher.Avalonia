using Avalonia.Controls;

namespace VibrantbitLauncher.Models;

/// <summary>
/// 设置搜索命中的一条。
///
/// <para>
/// 只由设置页的代码后置创建（索引是从视觉树里扫出来的 <c>SettingRow</c> 控件，
/// 没有可以绑定到 ViewModel 的数据来源），所以这里直接持有控件引用本身，
/// 点击时拿来滚动定位与高亮。
/// </para>
/// </summary>
public sealed class SettingSearchHit
{
    public SettingSearchHit(string header, string section, int sectionIndex, Control target)
    {
        Header = header;
        Section = section;
        SectionIndex = sectionIndex;
        Target = target;
    }

    /// <summary>这一行的标题，也就是搜索结果显示的主文本。</summary>
    public string Header { get; }

    /// <summary>它属于哪一节（通用 / 游戏 / 外观…），显示在标题下方。</summary>
    public string Section { get; }

    /// <summary>对应左侧分类的序号，点进去时用它切面板。</summary>
    public int SectionIndex { get; }

    /// <summary>要滚动到并高亮的那个控件。</summary>
    public Control Target { get; }
}
