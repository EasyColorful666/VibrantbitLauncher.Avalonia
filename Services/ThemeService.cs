using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Serilog;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Models;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 主题包的发现、导入与切换。
///
/// <para>
/// <b>主题包是什么</b>：一个目录（或一份 zip），里面是
/// <c>theme.json</c> 加上一到四份 XAML。XAML 就是 Avalonia 原生的
/// <see cref="ResourceDictionary"/> 与 <see cref="Styles"/>，
/// 不需要学任何自定义 DSL —— 会写 Avalonia 样式就会写主题。
/// </para>
///
/// <para>
/// <b>为什么不用键值对照表</b>：配色能靠一张「键 → 颜色」的表解决，
/// 但 PCL2 那种直角粗描边、Material 3 那种填充按钮，改的是控件模板与状态色，
/// 只有样式表能表达。所以主题包的 <c>styles.axaml</c> 是完整的 <see cref="Styles"/>，
/// 追加到应用样式的最后（优先级最高，能压过内置外观）。
/// </para>
///
/// <para>
/// <b>覆盖怎么撤销</b>：主题声明的键是<b>写进根资源字典</b>的（那层优先级高于
/// <c>MergedDictionaries</c> 里的 AppTheme / Surfaces，也高于 <c>ApplyTheme</c>
/// 手写的那几个窗口底色）。写之前把原值记进基线；换主题或退回默认时先按基线还原，
/// 不会留下上一套主题的颜色残影。
/// </para>
/// </summary>
public static class ThemeService
{
    /// <summary>内置默认主题的 id。它的「什么都不覆盖」，等价于没装主题。</summary>
    public const string DefaultId = "default";

    private const string BuiltInBase = "avares://VibrantbitLauncher.Avalonia/Assets/Themes/";

    /// <summary>主题目录名只允许这些字符 —— 它同时是路径的一段，不能让用户塞进 <c>..</c>。</summary>
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);

    private static readonly List<ThemePackage> _packages = new();

    /// <summary>覆盖前该键的原值。<see cref="Missing"/> 表示这个键本来就不存在，还原时要删掉。</summary>
    private static readonly Dictionary<string, object?> _baseline = new(StringComparer.Ordinal);

    private static readonly object Missing = new();

    /// <summary>解析过的资源字典（按「主题 id + 明暗」缓存）。拖动主题色滑杆会高频重放，不能每次都读盘解 XAML。</summary>
    private static readonly Dictionary<string, ResourceDictionary?> _dictCache = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, Styles?> _styleCache = new(StringComparer.Ordinal);

    private static Styles? _appliedStyles;
    private static bool _initialized;

    /// <summary>当前生效的主题包（找不到时回退内置默认）。</summary>
    private static ThemePackage? _active;

    /// <summary>主题切换后触发：主窗口据此刷新导航栏模式，设置页据此刷新选中项。</summary>
    public static event Action? Changed;

    /// <summary>用户主题目录（<c>&lt;程序目录&gt;/VBL/themes</c>）。</summary>
    public static string UserThemeDirectory =>
        Path.Combine(AppContext.BaseDirectory, "VBL", "themes");

    /// <summary>全部可选主题：内置的在前，用户装的在后。</summary>
    public static IReadOnlyList<ThemePackage> Packages
    {
        get
        {
            EnsureInitialized();
            return _packages;
        }
    }

    /// <summary>当前主题。永远非 null —— 配置里写了一个不存在的 id 时回退默认。</summary>
    public static ThemePackage Active
    {
        get
        {
            EnsureInitialized();
            return _active ?? _packages[0];
        }
    }

    // ============================ 初始化 ============================

    /// <summary>
    /// 扫一遍内置与用户主题目录。可以重复调用（导入 / 删除之后要重扫）。
    /// </summary>
    public static void Initialize()
    {
        // 每次重扫都丢掉解析缓存：用户改完自己的 theme.axaml 再进设置页，
        // 应该立刻看到新样子，而不是被上一次的解析结果挡住
        _dictCache.Clear();
        _styleCache.Clear();

        _packages.Clear();
        _packages.AddRange(BuiltInThemes());

        foreach (var package in UserThemes())
        {
            // 用户主题不能用内置 id 顶掉内置主题
            if (_packages.Any(p => p.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Warning("主题包 id 与内置主题重复，已跳过：{Id}", package.Id);
                continue;
            }

            _packages.Add(package);
        }

        if (_packages.Count == 0)
            _packages.Add(FallbackPackage());

        _active = Resolve(SettingsService.Current.ThemePackage);
        _initialized = true;

        Log.Information("主题包已扫描：{Count} 套，当前 {Id}", _packages.Count, _active.Id);
    }

    private static void EnsureInitialized()
    {
        if (!_initialized)
            Initialize();
    }

    /// <summary>按 id 取主题，取不到就回退默认。</summary>
    public static ThemePackage Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var hit = _packages.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                return hit;
        }

        return _packages.FirstOrDefault(p => p.Id == DefaultId) ?? _packages[0];
    }

    // ============================ 应用 ============================

    /// <summary>
    /// 把当前主题应用到界面。幂等 —— <c>SettingsService.ApplyTheme</c> 每次跑完都会调它，
    /// 因为那边会重写窗口底色与强调色，主题必须后写一次才能赢。
    /// </summary>
    public static void Apply()
    {
        var app = Application.Current;
        if (app is null)
            return;

        EnsureInitialized();

        // 每次都按**配置里的 id** 解析，而不是用上次缓存的那个：
        // 「恢复默认设置」「本区恢复默认」会把 ThemePackage 改回 default，
        // 那时没人来通知这里换主题，只能以配置为准。多一次查表，换来的是不会
        // 出现「设置里已经是默认，界面还停在上一套主题」的僵局。
        var theme = Resolve(SettingsService.Current.ThemePackage);
        _active = theme;

        RestoreBaseline(app);
        var applied = ApplyResources(app, theme);
        ApplyStyles(app, theme);

        // 排障用：一套主题「看着生效了、其实一条资源都没进来」是非常安静的故障
        // （1.0.23.0 就撞过一次 —— theme.json 读得到、XAML 读不到，配色全靠默认值）
        Log.Information("主题包已应用：{Id}（资源键 {Keys} 个，样式 {HasStyles}）",
            theme.Id, applied, _appliedStyles is not null);

    }

    /// <summary>切换主题：写配置、应用、通知各界刷新。</summary>
    public static void SetTheme(string id)
    {
        EnsureInitialized();

        var theme = Resolve(id);
        if (theme.Id != SettingsService.Current.ThemePackage)
        {
            SettingsService.Current.ThemePackage = theme.Id;
            SettingsService.Save();
        }

        _active = theme;

        // 走完整的 ApplyTheme：主题可能带强调色，重算整条派生链
        SettingsService.ApplyTheme();
        Apply();

        Changed?.Invoke();
        Log.Information("主题已切换：{Id}（{Name}）", theme.Id, theme.Name);
    }

    /// <summary>主题自带的强调色。没有就返回 false，继续用用户在设置里选的颜色。</summary>
    public static bool TryGetAccent(out Color color)
    {
        color = default;

        var raw = (_active ?? Resolve(SettingsService.Current.ThemePackage)).Accent;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return Color.TryParse(raw.Trim(), out color);
    }

    /// <summary>主题要求的导航栏模式。</summary>
    public static ThemeNavigation Navigation =>
        (_active ?? Resolve(SettingsService.Current.ThemePackage)).Navigation;

    private static void RestoreBaseline(Application app)
    {
        if (_baseline.Count == 0)
            return;

        foreach (var (key, value) in _baseline)
        {
            try
            {
                if (ReferenceEquals(value, Missing))
                    app.Resources.Remove(key);
                else if (value is not null)
                    app.Resources[key] = value;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "还原主题资源失败：{Key}", key);
            }
        }

        _baseline.Clear();
    }

    private static int ApplyResources(Application app, ThemePackage theme)
    {
        if (theme.Id == DefaultId)
            return 0;

        var applied = 0;

        foreach (var (key, value) in ReadEntries(theme))
        {
            if (string.IsNullOrEmpty(key) || value is null)
                continue;

            if (!_baseline.ContainsKey(key))
                _baseline[key] = Snapshot(app, key);

            try
            {
                app.Resources[key] = value;
                applied++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "写入主题资源失败：{Key}", key);
            }
        }

        return applied;
    }

    /// <summary>记下这个键当前的值（可能是主题字典 / 内置样式 / 压根没有）。</summary>
    private static object Snapshot(Application app, string key)
    {
        try
        {
            if (app.TryFindResource(key, app.ActualThemeVariant, out var existing) && existing is not null)
                return existing;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取资源原值失败：{Key}", key);
        }

        return Missing;
    }

    private static void ApplyStyles(Application app, ThemePackage theme)
    {
        var styles = theme.Id == DefaultId ? null : LoadStyles(theme);

        // 还是同一份样式对象：不动。切明暗、改强调色都会重放一遍，
        // 每次都摘掉再挂回去会让控件模板重新求值一次（视觉上是全窗口闪一下）
        if (ReferenceEquals(_appliedStyles, styles))
            return;

        if (_appliedStyles is not null)
            app.Styles.Remove(_appliedStyles);

        _appliedStyles = styles;

        if (styles is null)
            return;

        // 加到最后一个：应用样式里同具体度的选择器，后加的那个赢。
        // 主题要能压过 Styles/Controls.axaml 的内置外观，就只能站在队尾。
        app.Styles.Add(styles);
    }

    // ============================ 读取主题文件 ============================

    /// <summary>按当前明暗读出这一套要覆盖的键值。顺序：通用 → 明暗专用（后者赢）。</summary>
    private static IEnumerable<(string Key, object? Value)> ReadEntries(ThemePackage theme)
    {
        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

        foreach (var file in new[] { "theme.axaml", dark ? "dark.axaml" : "light.axaml" })
        {
            var dict = LoadResourceDictionary(theme, file);
            if (dict is null)
                continue;

            foreach (var pair in dict)
            {
                if (pair.Key is string key && key.Length > 0)
                    yield return (key, pair.Value);
            }
        }
    }

    private static ResourceDictionary? LoadResourceDictionary(ThemePackage theme, string fileName)
    {
        var cacheKey = theme.Id + "|" + fileName + "|" + (Application.Current?.ActualThemeVariant.ToString() ?? "");

        if (_dictCache.TryGetValue(cacheKey, out var cached))
            return cached;

        ResourceDictionary? result = null;

        try
        {
            var text = ReadThemeText(theme, fileName);
            if (text is not null)
                result = AvaloniaRuntimeXamlLoader.Load(text, localAssembly: typeof(ThemeService).Assembly, uri: BaseUriOf(theme)) as ResourceDictionary;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "主题资源加载失败：{Theme}/{File}", theme.Id, fileName);
        }

        _dictCache[cacheKey] = result;
        return result;
    }

    private static Styles? LoadStyles(ThemePackage theme)
    {
        var cacheKey = theme.Id + "|styles";

        if (_styleCache.TryGetValue(cacheKey, out var cached))
            return cached;

        Styles? result = null;

        try
        {
            var text = ReadThemeText(theme, "styles.axaml");
            if (text is not null)
                result = AvaloniaRuntimeXamlLoader.Load(text, localAssembly: typeof(ThemeService).Assembly, uri: BaseUriOf(theme)) as Styles;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "主题样式加载失败：{Theme}", theme.Id);
        }

        _styleCache[cacheKey] = result;
        return result;
    }

    /// <summary>读一份主题文件的文本。内置主题从程序集里读，用户主题从磁盘读。</summary>
    private static string? ReadThemeText(ThemePackage theme, string fileName)
    {
        if (theme.IsBuiltIn)
            return ReadBuiltInFile(theme.Id, fileName);

        var path = Path.Combine(theme.Directory, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    /// 读一份内置主题文件（<c>Assets/Themes/&lt;id&gt;/&lt;file&gt;</c>）。
    ///
    /// <para>
    /// <b>为什么走 EmbeddedResource 而不是 avares：</b>主题包里的 <c>.axaml</c> 必须是**原始文本**
    /// —— 运行期要用 <see cref="AvaloniaRuntimeXamlLoader"/> 解析它，内置那三套才和用户写的
    /// 主题包是同一种东西。但只要有 <c>.axaml</c> 扩展名，Avalonia 的构建任务就会把它当编译期
    /// XAML 处理，最后既没有编译产物、也没留下原文（1.0.23.0 的坑：主题配色一条都没生效）。
    /// 所以在 csproj 里把它们从 Avalonia 资源里摘出来、按普通嵌入式资源打包，
    /// 这里按「.主题id.文件名」后缀在清单里找。
    /// </para>
    /// </summary>
    private static string? ReadBuiltInFile(string id, string fileName)
    {
        var assembly = typeof(ThemeService).Assembly;

        try
        {
            // 默认资源名是「RootNamespace.Assets.Themes.<id>.<file>」，
            // 前缀不参与比较 —— 改 RootNamespace 不该让主题集体失效
            var suffix = "." + id + "." + fileName;

            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                    continue;

                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取内置主题文件失败：{Theme}/{File}", id, fileName);
        }

        // 兜底：万一哪天又把它们放回 Avalonia 资源里，这条路仍然是通的
        try
        {
            var uri = new Uri($"{BuiltInBase}{id}/{fileName}");
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "从 Avalonia 资源读取内置主题失败：{Theme}/{File}", id, fileName);
        }

        return null;
    }

    /// <summary>
    /// 主题里写相对 URI（比如预览图）时的基准。
    /// 用户主题没有可靠的程序集 URI，给 null —— 于是相对引用会失败，但不会崩。
    /// </summary>
    private static Uri? BaseUriOf(ThemePackage theme)
        => theme.IsBuiltIn ? new Uri($"{BuiltInBase}{theme.Id}/") : null;

    // ============================ 内置主题 ============================

    private static IEnumerable<ThemePackage> BuiltInThemes()
    {
        var dir = new Uri(BuiltInBase);
        var ids = new[] { DefaultId, "pcl2", "material3" };

        foreach (var id in ids)
        {
            var package = ReadPackage(id, themesRoot: BuiltInBase, directory: string.Empty, isBuiltIn: true);

            // 元信息缺失也要能出列 —— 内置主题不允许「配置文件写坏就整个消失」
            yield return package ?? new ThemePackage
            {
                Id = id,
                Name = id,
                IsBuiltIn = true,
                Navigation = id == "pcl2" ? ThemeNavigation.Top : ThemeNavigation.Compact
            };
        }
    }

    // ============================ 用户主题 ============================

    private static IEnumerable<ThemePackage> UserThemes()
    {
        var root = UserThemeDirectory;
        if (!Directory.Exists(root))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var package = ReadPackage(
                Path.GetFileName(dir),
                themesRoot: root,
                directory: dir,
                isBuiltIn: false);

            if (package is null)
            {
                Log.Warning("主题包不完整，已跳过：{Dir}", dir);
                continue;
            }

            yield return package;
        }
    }

    // ============================ theme.json ============================

    /// <summary>
    /// 读一份 <c>theme.json</c>。返回 null 表示这套主题不可用（缺文件 / id 不合法 / 解析失败）。
    /// </summary>
    private static ThemePackage? ReadPackage(string id, string themesRoot, string directory, bool isBuiltIn)
    {
        if (!IdPattern.IsMatch(id))
        {
            Log.Warning("主题 id 不合法：{Id}", id);
            return null;
        }

        string? json = null;

        try
        {
            if (isBuiltIn)
            {
                json = ReadBuiltInFile(id, "theme.json");
            }
            else
            {
                var path = Path.Combine(directory, "theme.json");
                if (File.Exists(path))
                    json = File.ReadAllText(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取主题元信息失败：{Id}", id);
        }

        if (json is null)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return new ThemePackage
            {
                Id = id,
                Name = String(root, "name") ?? id,
                Author = String(root, "author") ?? string.Empty,
                Version = String(root, "version") ?? string.Empty,
                Description = String(root, "description") ?? string.Empty,
                Accent = String(root, "accent"),
                Navigation = ParseNavigation(String(root, "navigation")),
                IsBuiltIn = isBuiltIn,
                Directory = directory,
                PreviewPath = String(root, "preview")
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "主题元信息解析失败：{Id}", id);
            return null;
        }

        static string? String(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static ThemeNavigation ParseNavigation(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "left" => ThemeNavigation.Left,
        "top" => ThemeNavigation.Top,
        "header" => ThemeNavigation.Header,
        "minimal" => ThemeNavigation.Minimal,
        _ => ThemeNavigation.Compact
    };

    private static ThemePackage FallbackPackage() => new()
    {
        Id = DefaultId,
        Name = "默认",
        IsBuiltIn = true
    };

    // ============================ 导入 / 删除 ============================

    /// <summary>
    /// 导入一份主题包。支持 zip 与文件夹两种形态。
    ///
    /// <para>
    /// 校验只看三件事：目录里能找到 <c>theme.json</c>、id 合法、至少有一份 XAML。
    /// XAML 内容不做静态审查 —— 它会被 Avalonia 真的执行，和「运行一个程序」是同一件事，
    /// 界面上已经写明「只装你信得过的主题」。
    /// </para>
    /// </summary>
    public static async Task<(bool Ok, string Message, ThemePackage? Package)> ImportAsync(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return (false, "找不到这个文件或文件夹", null);

            var staging = Path.Combine(Path.GetTempPath(), "vbl-theme-" + Guid.NewGuid().ToString("N"));

            try
            {
                if (Directory.Exists(path))
                {
                    CopyDirectory(path, staging);
                }
                else if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(path, staging);
                }
                else
                {
                    return (false, "只支持 .zip 或文件夹形式的主题包", null);
                }

                // zip 常常把文件套在一层目录里，往下找一层
                var source = FindThemeRoot(staging);
                if (source is null)
                    return (false, "主题包里没有 theme.json", null);

                var id = ReadIdFrom(source);
                if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id))
                    return (false, "theme.json 里的 id 只能用小写字母、数字或连字符，以字母或数字开头，最长 64 个字符", null);

                if (!Directory.EnumerateFiles(source, "*.axaml", SearchOption.AllDirectories).Any())
                    return (false, "主题包里没有任何 .axaml，装了也不会改变外观", null);

                // 不允许顶掉内置主题
                if (await Task.Run(() => BuiltInThemes().Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
                    return (false, $"「{id}」是内置主题的 id，换一个", null);

                var target = Path.Combine(UserThemeDirectory, id);

                await Task.Run(() =>
                {
                    Directory.CreateDirectory(UserThemeDirectory);
                    if (Directory.Exists(target))
                        Directory.Delete(target, recursive: true);

                    CopyDirectory(source, target);
                });

                Initialize();

                var package = _packages.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                return package is null
                    ? (false, "主题已复制进去，但读取不到，检查 theme.json", null)
                    : (true, $"已导入「{package.Name}」", package);
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导入主题包失败：{Path}", path);
            return (false, "导入失败：" + FriendlyError.Describe(ex), null);
        }
    }

    /// <summary>删掉一套用户主题。内置主题拒绝。</summary>
    public static bool Delete(string id)
    {
        EnsureInitialized();

        var package = _packages.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (package is null || package.IsBuiltIn)
            return false;

        // 正在用的那套被删掉：先退回默认，界面不能停在半截状态
        if (string.Equals(SettingsService.Current.ThemePackage, package.Id, StringComparison.OrdinalIgnoreCase))
        {
            SettingsService.Current.ThemePackage = DefaultId;
            SettingsService.Save();
        }

        try
        {
            if (Directory.Exists(package.Directory))
                Directory.Delete(package.Directory, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "删除主题包失败：{Id}", id);
            return false;
        }

        _dictCache.Clear();
        _styleCache.Clear();
        Initialize();

        var active = Resolve(SettingsService.Current.ThemePackage);
        _active = active;
        SettingsService.ApplyTheme();
        Apply();
        Changed?.Invoke();

        Log.Information("主题包已删除：{Id}", id);
        return true;
    }

    /// <summary>打开主题目录（不存在就建一个），方便用户直接往里放。</summary>
    public static void OpenThemeDirectory()
    {
        try
        {
            Directory.CreateDirectory(UserThemeDirectory);
            EnsureExampleTemplate();

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = UserThemeDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开主题目录失败：{Dir}", UserThemeDirectory);
        }
    }

    /// <summary>
    /// 目录还是空的时候，放一份带注释的示例主题进去。
    ///
    /// <para>
    /// 与其让人去翻文档猜 <c>theme.json</c> 该写什么，不如让他一打开目录就看到一份能跑的东西：
    /// 复制一份、改个 id、把颜色换成自己的，就是一套新主题。只在目录为空时写，
    /// 用户删掉它也不会反复冒出来。
    /// </para>
    /// </summary>
    private static void EnsureExampleTemplate()
    {
        try
        {
            if (Directory.EnumerateFileSystemEntries(UserThemeDirectory).Any())
                return;

            var dir = Path.Combine(UserThemeDirectory, "example");
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, "theme.json"), """
            {
              "id": "example",
              "name": "示例主题（复制我改一改）",
              "author": "你的名字",
              "version": "1.0",
              "description": "把这个文件夹整个复制一份、改掉 id 和配色，就是你自己的一套主题。",
              "navigation": "compact",
              "accent": "#FF128BFA"
            }
            """);

            File.WriteAllText(Path.Combine(dir, "light.axaml"), """
            <ResourceDictionary xmlns="https://github.com/avaloniaui"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <!--
                    亮色下覆盖的资源。键名与 Styles/AppTheme.axaml、Styles/Surfaces.axaml 一致，
                    写在这里的会盖掉默认值。

                    常用键：
                      SurfaceCanvasBrush   窗口画布
                      SurfaceCardBrush     卡片底色
                      SurfaceGlassBrush    浮层 / 侧栏
                      HairlineBrush        1px 细线
                      AppAccentSurfaceBrush 强调色浅底
                -->
                <SolidColorBrush x:Key="SurfaceCanvasBrush" Color="#FFF7F8FA" />
                <SolidColorBrush x:Key="SurfaceCardBrush" Color="#FFFFFFFF" />
            </ResourceDictionary>
            """);

            File.WriteAllText(Path.Combine(dir, "dark.axaml"), """
            <ResourceDictionary xmlns="https://github.com/avaloniaui"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <!--  暗色下覆盖的资源。不写这个文件就两个明暗共用 light.axaml。  -->
                <SolidColorBrush x:Key="SurfaceCanvasBrush" Color="#FF15171B" />
                <SolidColorBrush x:Key="SurfaceCardBrush" Color="#FF212429" />
            </ResourceDictionary>
            """);

            File.WriteAllText(Path.Combine(dir, "styles.axaml"), """
            <Styles xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <!--
                    控件外观。这里写的就是 Avalonia 的样式表，选择器与内置的一致。
                    删掉整个文件也不会出错 —— 只换颜色也是很正常的主题。
                -->
                <Style Selector="Button">
                    <Setter Property="CornerRadius" Value="0" />
                </Style>
            </Styles>
            """);

            File.WriteAllText(Path.Combine(dir, "说明.txt"), """
            主题包结构（四份 XAML 都是可选的，theme.json 必需）：

              theme.json     元信息：id / name / author / version / description
                     navigation 导航栏模式：left | top | header | compact | minimal
                                header = 导航项并进标题栏（横向居中，配蓝色顶栏最像 PCL2）
                     accent     可选，主题自带的强调色，如 "#FF128BFA"
              theme.axaml    所有明暗都生效的资源覆盖（ResourceDictionary）
              light.axaml    只在亮色下生效的资源覆盖
              dark.axaml     只在暗色下生效的资源覆盖
              styles.axaml   控件外观（Styles），会追加到内置样式的最后，优先级最高

            id 只能用小写字母、数字、连字符，且不能和内置主题重名（default / pcl2 / material3）。

            改完回到「设置 → 外观 → 主题包」即可看到新的一套；已经在用的那套改完
            重进设置页会重新加载。主题包里的样式会被真正执行，只装你自己写的或信得过的来源。
            """);

            Log.Information("已生成主题包示例模板：{Dir}", dir);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "生成主题示例模板失败");
        }
    }

    private static string? FindThemeRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "theme.json")))
            return dir;

        foreach (var child in Directory.EnumerateDirectories(dir))
        {
            if (File.Exists(Path.Combine(child, "theme.json")))
                return child;
        }

        return null;
    }

    private static string? ReadIdFrom(string dir)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "theme.json")));
            return document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()?.Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "清理临时目录失败：{Dir}", dir);
        }
    }
}
