namespace VibrantbitLauncher.Localization.Translations;

/// <summary>
/// 英文词表总入口。
///
/// <para>
/// 按区域拆成多个 partial 文件，只为「改某一页的文案时不必在两千行里翻」。
/// 键 = 中文原文，值 = 英文译文；同一个键在多个文件里重复时后加载的覆盖前面的，
/// 但这种情况不该存在 —— 出现就说明有一处译文该删。
/// </para>
/// </summary>
internal static class TranslationTable
{
    internal static readonly Dictionary<string, string> En = new(StringComparer.Ordinal);

    private static bool _loaded;

    public static void Load()
    {
        if (_loaded)
            return;

        _loaded = true;

        UiShell.AddTo(En);
        UiSettings.AddTo(En);
        UiTools.AddTo(En);
        UiMessages1.AddTo(En);
        UiMessages2.AddTo(En);
        UiMessages3.AddTo(En);
        UiMessages4.AddTo(En);
    }
}
