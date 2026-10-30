using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Serilog;
using VibrantbitLauncher.Localization.Translations;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Localization;

/// <summary>界面语言。</summary>
public enum AppLanguage
{
    Chinese,
    English
}

/// <summary>
/// 运行时翻译。
///
/// <para>
/// <b>为什么用中文原文当键：</b>这个项目所有界面文案本来就是写死的中文，
/// 若改成「键 → 译文」，几百个 XAML 属性全要重写一遍，改错一处就是空白界面。
/// 反过来把中文原文当键，XAML 一个字都不用动，词典只是「中文 → English」的对照表：
/// 表里有就换掉，没有就原样显示。这样<b>漏翻的后果是「还是中文」</b>，而不是空白或 Key 名。
/// </para>
///
/// <para>
/// <b>动态文案怎么办：</b>代码里大量 <c>$"共 {n} 个存档"</c> 这种拼好的句子，
/// 到界面时已经带上了数字，按原文查表永远查不到。所以词典里的键可以带 <c>{0}</c> 占位，
/// 查不到原文时再拿这些「带洞的键」当正则去套，套中了就把捕获组填进对应译文。
/// 结果按输入串缓存，同一句话只匹配一次。
/// </para>
///
/// <para>
/// <b>怎么覆盖到界面：</b>不去扫描视觉树，而是给 <c>TextBlock.Text</c>、<c>ContentControl.Content</c>
/// 这类属性挂全局的「属性变化」处理器 —— 译文在值写入的那一刻就替换掉，
/// 数据模板里后生成的元素、运行时才赋值的状态栏文字都自动包含在内。
/// 切换语言时再遍历一次打开着的窗口，把已经翻译过的元素按记下的原文重算。
/// </para>
/// </summary>
public static class Localize
{
    /// <summary>元素当前语言下的原文（中文）。切换语言时靠它重算。</summary>
    private static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("SourceText", typeof(Localize));

    /// <summary>
    /// 悬挂提示的原文。
    ///
    /// 必须和正文分开存：一个按钮同时有 <c>Content</c> 和 <c>ToolTip.Tip</c>，
    /// 共用一个槽的话后者会把前者的原文冲掉，切换语言时按钮标题就再也回不来了。
    /// </summary>
    private static readonly AttachedProperty<string?> ToolTipSourceProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("SourceToolTip", typeof(Localize));


    /// <summary>「这次赋值是我们自己写的」，用来掐断属性变化处理器的重入。</summary>
    private static readonly AttachedProperty<bool> BusyProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Translating", typeof(Localize));

    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly List<PatternEntry> Patterns = new();

    /// <summary>已经算过的动态文案，避免同一句话反复走正则。</summary>
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    private static bool _hooked;
    private static bool _ready;

    /// <summary>当前语言。</summary>
    public static AppLanguage Current { get; private set; } = AppLanguage.Chinese;

    public static bool IsEnglish => Current == AppLanguage.English;

    /// <summary>语言变了（界面已经重算完）。托盘菜单之类自己建文字的地方要跟着刷新。</summary>
    public static event Action? Changed;

    // ============================ 对外 ============================

    /// <summary>
    /// 启动时调一次：定下语言并把「属性变化」处理器挂上。
    ///
    /// <para>
    /// 语言来源是设置里那一项；从没选过的老配置留空，此时按系统语言猜一次
    /// （中文系统给中文，其它一律英文），猜的结果<b>不写回设置</b> —— 玩家真正去选了才算数。
    /// </para>
    /// </summary>
    public static void Initialize()
    {
        if (_ready)
            return;

        Current = ResolveLanguage(SettingsService.Current.Language);

        TranslationTable.Load();

        var patterns = new List<PatternEntry>();
        foreach (var (zh, en) in TranslationTable.En)
        {
            if (zh.Length == 0)
                continue;

            if (zh.Contains('{'))
                patterns.Add(PatternEntry.Create(zh, en));
            else
                Exact[zh] = en;
        }

        // 字面量长的先试。
        // 「约 {0} 秒」和「{0} 秒」都能套住「约 42 秒」，但前者能整句翻掉；
        // 若按插入顺序走，短的先命中就会得到「约 42 s」这种半中半英。
        patterns.Sort((a, b) => b.LiteralLength.CompareTo(a.LiteralLength));
        Patterns.AddRange(patterns);

        Hook();
        _ready = true;

        Log.Information("界面语言：{Lang}（词条 {Exact} 条 + 模式 {Patterns} 条）",
            Current, Exact.Count, Patterns.Count);
    }

    /// <summary>切换语言并让所有打开的窗口立刻重算。</summary>
    public static void SetLanguage(AppLanguage language)
    {
        if (!_ready || language == Current)
            return;

        Current = language;
        Cache.Clear();

        // Avalonia 12 里窗口列表挂在生命周期对象上，Application 本身没有 Windows
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
                Reapply(window);
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "语言切换通知发出失败");
        }
    }

    /// <summary>取译文；没有对应词条时返回原文。</summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text) || Current == AppLanguage.Chinese || !_ready)
            return text ?? string.Empty;

        if (Exact.TryGetValue(text, out var hit))
            return hit;

        if (Cache.TryGetValue(text, out var cached))
            return cached;

        var translated = MatchPattern(text);
        if (translated is not null)
        {
            Cache[text] = translated;
            return translated;
        }

        // 记下「查过了、没有」，下次不必再跑一遍正则
        Cache[text] = text;
        return text;
    }

    /// <summary>先翻译带占位符的模板，再把参数填进去。</summary>
    public static string Tf(string format, params object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, T(format), args);
        }
        catch (FormatException)
        {
            // 译文里占位符写错了也不能让界面崩，退回原文
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
    }

    /// <summary>只有一个占位符时的简写。</summary>
    public static string Tf(string format, object? arg) => Tf(format, new[] { arg });

    /// <summary>把某个元素（含子孙）按当前语言重算一遍。</summary>
    public static void Reapply(Visual root)
    {
        if (!_ready)
            return;

        Walk(root);

        static void Walk(Visual visual)
        {
            ApplyElement(visual);

            foreach (var child in visual.GetVisualChildren())
                Walk(child);
        }
    }

    // ============================ 属性挂钩 ============================

    /// <summary>
    /// 给「会显示文字」的几类属性挂全局处理器。
    ///
    /// 这一层代替了「遍历视觉树找文字」，好处是数据模板 / 虚拟化列表里后生成的元素也覆盖得到。
    /// </summary>
    private static void Hook()
    {
        if (_hooked)
            return;

        _hooked = true;

        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>(
            (tb, e) => OnTextChanged(tb, e, TextBlock.TextProperty, () => tb.Text, v => tb.Text = v));

        ContentControl.ContentProperty.Changed.AddClassHandler<ContentControl>(
            (c, e) => OnContentChanged(c, e));

        HeaderedContentControl.HeaderProperty.Changed.AddClassHandler<HeaderedContentControl>(
            (c, e) => OnTextChanged(c, e, HeaderedContentControl.HeaderProperty, () => c.Header as string, v => c.Header = v));

        HeaderedSelectingItemsControl.HeaderProperty.Changed.AddClassHandler<HeaderedSelectingItemsControl>(
            (c, e) => OnTextChanged(c, e, HeaderedSelectingItemsControl.HeaderProperty, () => c.Header as string, v => c.Header = v));

        TextBox.PlaceholderTextProperty.Changed.AddClassHandler<TextBox>(
            (t, e) => OnTextChanged(t, e, TextBox.PlaceholderTextProperty, () => t.PlaceholderText, v => t.PlaceholderText = v));

        Window.TitleProperty.Changed.AddClassHandler<Window>(
            (w, e) => OnTextChanged(w, e, Window.TitleProperty, () => w.Title, v => w.Title = v));

        // ToolTip 是附加属性，挂在任意控件上
        ToolTip.TipProperty.Changed.AddClassHandler<Control>(
            (c, e) => OnToolTipChanged(c, e));
    }

    /// <summary>
    /// 内容型控件（按钮 / 菜单项 / 卡片）的显示文字。
    ///
    /// <para>
    /// <b>列表项不在这里翻：</b><c>ListBoxItem</c>（含 <c>ComboBoxItem</c>）的
    /// <c>Content</c> 就是绑定的值本身，改掉它会让「选中的东西」变成英文再写回设置 ——
    /// 字体名、日志等级这些值一旦被翻译就直接失效。列表项里负责显示的那个
    /// <c>TextBlock</c> 会走自己的处理器，所以看得到的文字照样是英文，值却完好。
    /// </para>
    /// </summary>
    private static void OnContentChanged(ContentControl owner, AvaloniaPropertyChangedEventArgs e)
    {
        if (owner is ListBoxItem)
            return;

        OnTextChanged(owner, e, ContentControl.ContentProperty, () => owner.Content as string, v => owner.Content = v);
    }

    private static void OnTextChanged(
        Control owner,
        AvaloniaPropertyChangedEventArgs e,
        AvaloniaProperty property,
        Func<string?> read,
        Action<string> write)
    {
        if (e.Property != property || owner.GetValue(BusyProperty))
            return;

        var incoming = read();
        if (string.IsNullOrEmpty(incoming))
            return;

        // 记原文：不管这次翻不翻，最新写进来的中文就是「原文」
        owner.SetValue(SourceProperty, incoming);

        if (!IsEnglish)
            return;

        var translated = T(incoming);
        if (string.Equals(translated, incoming, StringComparison.Ordinal))
            return;

        owner.SetValue(BusyProperty, true);
        try
        {
            write(translated);
        }
        finally
        {
            owner.SetValue(BusyProperty, false);
        }
    }

    private static void OnToolTipChanged(Control owner, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ToolTip.TipProperty || owner.GetValue(BusyProperty))
            return;

        if (ToolTip.GetTip(owner) is not string tip || tip.Length == 0)
            return;

        owner.SetValue(ToolTipSourceProperty, tip);

        if (!IsEnglish)
            return;

        var translated = T(tip);
        if (string.Equals(translated, tip, StringComparison.Ordinal))
            return;

        owner.SetValue(BusyProperty, true);
        try
        {
            ToolTip.SetTip(owner, translated);
        }
        finally
        {
            owner.SetValue(BusyProperty, false);
        }
    }

    // ============================ 切换语言时的重算 ============================

    /// <summary>只处理「这个元素自己的文字属性」，子元素交给遍历。</summary>
    private static void ApplyElement(Visual visual)
    {
        if (visual is not Control control)
            return;

        var source = control.GetValue(SourceProperty);
        if (!string.IsNullOrEmpty(source))
        {
            switch (control)
            {
                case TextBlock textBlock:
                    Set(textBlock, source, () => textBlock.Text, v => textBlock.Text = v);
                    break;

                case Window window:
                    Set(window, source, () => window.Title, v => window.Title = v);
                    break;

                case TextBox textBox:
                    Set(textBox, source, () => textBox.PlaceholderText, v => textBox.PlaceholderText = v);
                    break;

                case HeaderedContentControl headered:
                    Set(headered, source, () => headered.Header as string, v => headered.Header = v);
                    break;

                case HeaderedSelectingItemsControl menu:
                    Set(menu, source, () => menu.Header as string, v => menu.Header = v);
                    break;

                // 列表项的 Content 是「值」不是「文案」，切换语言时同样不能碰（详见 OnContentChanged）
                case ListBoxItem:
                    break;

                case ContentControl content:
                    Set(content, source, () => content.Content as string, v => content.Content = v);
                    break;
            }
        }

        var tipSource = control.GetValue(ToolTipSourceProperty);
        if (!string.IsNullOrEmpty(tipSource))
        {
            var wanted = T(tipSource);
            if (!string.Equals(ToolTip.GetTip(control) as string, wanted, StringComparison.Ordinal))
            {
                control.SetValue(BusyProperty, true);
                try { ToolTip.SetTip(control, wanted); }
                finally { control.SetValue(BusyProperty, false); }
            }
        }
    }

    /// <summary>按原文把某个属性重设成当前语言的译文，已经对上就不动（避免多余的通知）。</summary>
    private static void Set(Control owner, string source, Func<string?> read, Action<string> write)
    {
        var wanted = T(source);
        if (string.Equals(read(), wanted, StringComparison.Ordinal))
            return;

        owner.SetValue(BusyProperty, true);
        try
        {
            write(wanted);
        }
        finally
        {
            owner.SetValue(BusyProperty, false);
        }
    }

    // ============================ 内部 ============================

    /// <summary>把带 <c>{0}</c> 的键当正则去套一句实际文案，套中了返回填好的译文。</summary>
    private static string? MatchPattern(string text)
    {
        foreach (var pattern in Patterns)
        {
            var match = pattern.Regex.Match(text);
            if (match.Success)
                return pattern.Render(match);
        }

        return null;
    }

    private static AppLanguage ResolveLanguage(string? configured)
    {
        if (string.Equals(configured, "en-US", StringComparison.OrdinalIgnoreCase))
            return AppLanguage.English;

        if (string.Equals(configured, "zh-CN", StringComparison.OrdinalIgnoreCase))
            return AppLanguage.Chinese;

        return DetectSystemLanguage();
    }

    /// <summary>
    /// 没选过语言时按系统猜。只看「当前区域名是不是以 zh 开头」——
    /// 比读注册表简单，也不会因为系统是「中文（新加坡）」这类给判错。
    /// </summary>
    public static AppLanguage DetectSystemLanguage()
    {
        try
        {
            var name = CultureInfo.CurrentUICulture.Name;
            return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.Chinese
                : AppLanguage.English;
        }
        catch
        {
            return AppLanguage.Chinese;
        }
    }

    /// <summary>一条「带占位符的词条」：键换成正则，译文按捕获组填空。</summary>
    private sealed class PatternEntry
    {
        private readonly string _english;
        private readonly int[] _holes;

        private PatternEntry(Regex regex, string english, int[] holes, int literalLength)
        {
            Regex = regex;
            _english = english;
            _holes = holes;
            LiteralLength = literalLength;
        }

        public Regex Regex { get; }

        /// <summary>键里除去占位符剩下多少个字 —— 越具体（越长）的模式越该先匹配。</summary>
        public int LiteralLength { get; }

        public static PatternEntry Create(string chinese, string english)
        {
            var holes = new List<int>();
            var builder = new StringBuilder("^");

            for (var i = 0; i < chinese.Length; i++)
            {
                var c = chinese[i];

                if (c == '{' && i + 2 < chinese.Length && char.IsDigit(chinese[i + 1]) && chinese[i + 2] == '}')
                {
                    holes.Add(chinese[i + 1] - '0');
                    builder.Append("(.*?)");
                    i += 2;
                    continue;
                }

                builder.Append(Regex.Escape(c.ToString()));
            }

            builder.Append('$');

            // 单行模式：文案里本来就不该有换行，有也当普通字符
            return new PatternEntry(
                new Regex(builder.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)),
                english,
                holes.ToArray(),
                chinese.Length - holes.Count * 3);
        }

        public string Render(Match match)
        {
            var result = new StringBuilder(_english);

            for (var i = 0; i < _holes.Length; i++)
            {
                var value = i + 1 < match.Groups.Count ? match.Groups[i + 1].Value : string.Empty;

                // 键里的 {n} 对应译文里的 {n}，这里直接把占位符换成实际值
                result.Replace("{" + _holes[i] + "}", value);
            }

            return result.ToString();
        }
    }
}
