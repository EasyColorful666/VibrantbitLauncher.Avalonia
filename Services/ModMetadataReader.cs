using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services;

/// <summary>一个已安装模组的元数据（从 jar 内部声明里读出来的）。</summary>
public sealed class InstalledMod
{
    /// <summary>jar 的完整路径。</summary>
    public string FilePath { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    /// <summary>模组 id（依赖声明里互相引用的就是它，例如 <c>fabric-api</c> / <c>sodium</c>）。</summary>
    public string ModId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;

    /// <summary>从哪个元数据文件读出来的（fabric.mod.json / mods.toml …），排错用。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// 这个 jar 声明的模组加载器（按命中顺序取第一个元数据文件对应的那种）。
    /// </summary>
    public ModLoaderKind Loader { get; init; } = ModLoaderKind.Unknown;

    /// <summary>
    /// jar 里**所有**可识别的加载器声明。
    ///
    /// <para>
    /// 有些 jar 同时带 <c>fabric.mod.json</c> 和 <c>META-INF/mods.toml</c>（跨加载器的兼容层，
    /// 或作者打了通用包）。判断「能不能在这个实例里加载」必须看这个集合，而不是只看主元数据：
    /// 只看主元数据会把这类双声明 jar 误判成「加载器不匹配」。
    /// </para>
    /// </summary>
    public List<ModLoaderKind> Loaders { get; init; } = new();

    /// <summary>它声明依赖的其它模组（不含 minecraft / java / 加载器这些内置项 —— 那些不参与「缺失」判定）。</summary>
    public List<ModDependency> Dependencies { get; init; } = new();

    public string Sha1 { get; init; } = string.Empty;
    public long Size { get; init; }
}

/// <summary>一条依赖声明。</summary>
public sealed class ModDependency
{
    /// <summary>被依赖的模组 id。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>版本范围原文（如 <c>&gt;=1.2.0</c> / <c>[1.20.1,1.21)</c>），可能为空。</summary>
    public string VersionRange { get; init; } = string.Empty;

    /// <summary>是否必需（Forge 的 mandatory；Fabric / Quilt 的 depends 一律视为必需）。</summary>
    public bool Mandatory { get; init; } = true;

    /// <summary>是否只在客户端必需（side=CLIENT / BOTH）。服务端侧的依赖不该在客户端实例里报缺失。</summary>
    public bool ClientSide { get; init; } = true;
}

/// <summary>
/// 从模组 jar 里读元数据。
///
/// <para>
/// 支持四种声明（覆盖全部主流加载器）：
/// <list type="bullet">
/// <item><b>Fabric</b>：jar 根目录 <c>fabric.mod.json</c>（<c>id</c> / <c>version</c> / <c>depends</c>）</item>
/// <item><b>Quilt</b>：jar 根目录 <c>quilt.mod.json</c>（<c>quilt_loader.id</c> / <c>quilt_loader.depends</c>）</item>
/// <item><b>Forge</b>：<c>META-INF/mods.toml</c>（<c>[[mods]]</c> / <c>[[dependencies.&lt;id&gt;]]</c>）</item>
/// <item><b>NeoForge</b>：<c>META-INF/neoforge.mods.toml</c>（与 Forge 同构）</item>
/// </list>
/// 一个 jar 里可能同时存在多种声明（比如 Fabric 模组也带 mods.toml 的兼容层），按上述顺序取第一个命中的。
/// </para>
///
/// <para>
/// <b>不抛异常</b>：单个 jar 损坏 / 不是模组 / 是嵌套库，一律返回 null。调用方都是 UI 路径，
/// 「读不出来」应当表现为「这个文件参与不了体检」，而不是把整页带崩。
/// </para>
/// </summary>
public static class ModMetadataReader
{
    /// <summary>
    /// 内置的「提供者」id —— 这些由游戏 / 加载器本身提供，不算缺失。
    /// 名称按各加载器社区里最常见的写法列全，比较时统一小写。
    /// </summary>
    private static readonly HashSet<string> BuiltInProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "mcp", "fml", "forge", "neoforge", "fabricloader", "fabric-loader",
        "fabric_loader", "quilt_loader", "quiltloader", "quilt-loader", "minecraftforge",
        "optifine", "optifabric", "fabric", "quilt", "liteloader", "modlauncher", "bootstraplauncher",
        "client", "server", "cloth-config-hints", // 少数模组会声明对它的软依赖，实际并不存在此模组
    };

    /// <summary>这个 id 是否由游戏 / 加载器提供（不需要装额外模组）。</summary>
    public static bool IsBuiltIn(string modId)
        => BuiltInProviders.Contains(NormalizeId(modId));

    /// <summary>读一个 jar 的模组元数据；不是模组 / 读不出返回 null。</summary>
    public static InstalledMod? TryRead(string jarPath)
    {
        if (string.IsNullOrWhiteSpace(jarPath) || !File.Exists(jarPath))
            return null;

        try
        {
            using var archive = ZipFile.OpenRead(jarPath);

            // 三种声明都要尝试，不再「命中第一个就返回」：
            // 既要拿到主元数据（id / name / version / 依赖），也要知道这个 jar 支持哪些加载器。
            var fabric = ReadFabric(archive);
            var quilt = ReadQuilt(archive);
            var toml = ReadToml(archive);

            var primary = fabric ?? quilt ?? toml;
            if (primary is null)
                return null;

            var loaders = new List<ModLoaderKind>(3);
            if (fabric is not null) loaders.Add(ModLoaderKind.Fabric);
            if (quilt is not null) loaders.Add(ModLoaderKind.Quilt);
            if (toml is not null) loaders.Add(toml.Value.Loader);

            var file = new FileInfo(jarPath);
            return new InstalledMod
            {
                FilePath = jarPath,
                FileName = file.Name,
                ModId = primary.Value.ModId,
                Name = string.IsNullOrWhiteSpace(primary.Value.Name) ? primary.Value.ModId : primary.Value.Name,
                Version = primary.Value.Version,
                Source = primary.Value.Source,
                Loader = primary.Value.Loader,
                Loaders = loaders,
                Dependencies = primary.Value.Dependencies,
                Sha1 = TryComputeSha1(file),
                Size = file.Length,
            };
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "读取模组元数据失败：{Jar}", jarPath);
            return null;
        }
    }

    /// <summary>扫描一个目录下的全部 jar（不递归子目录）。</summary>
    public static List<InstalledMod> ReadAll(string modsFolder)
    {
        var result = new List<InstalledMod>();
        if (string.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder))
            return result;

        string[] files;
        try
        {
            // .disabled 后缀的文件游戏不会加载，不算「已安装」
            files = Directory.GetFiles(modsFolder, "*.jar", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "枚举模组目录失败：{Dir}", modsFolder);
            return result;
        }

        foreach (var f in files)
        {
            var mod = TryRead(f);
            if (mod is not null)
                result.Add(mod);
        }

        return result;
    }

    // ==================== Fabric ====================

    private static MetaResult? ReadFabric(ZipArchive archive)
    {
        var entry = FindEntry(archive, "fabric.mod.json");
        if (entry is null)
            return null;

        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var id = Str(root, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var deps = new List<ModDependency>();
        if (root.TryGetProperty("depends", out var depends))
            deps.AddRange(ParseFabricDeps(depends));

        return new MetaResult(id, Str(root, "name"), Str(root, "version"), "fabric.mod.json",
            ModLoaderKind.Fabric, deps);
    }

    /// <summary>
    /// Fabric 的 depends 有两种写法：对象（<c>{"id": "range"}</c>）和数组
    /// （<c>[{"id": "...", "versions": "..."}]</c>，1.20 之后新增）。两种都要认。
    /// </summary>
    private static IEnumerable<ModDependency> ParseFabricDeps(JsonElement depends)
    {
        if (depends.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in depends.EnumerateObject())
                yield return new ModDependency { Id = p.Name, VersionRange = AsString(p.Value), Mandatory = true };
            yield break;
        }

        if (depends.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in depends.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    yield return new ModDependency { Id = item.GetString() ?? string.Empty, Mandatory = true };
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return new ModDependency
                    {
                        Id = Str(item, "id"),
                        VersionRange = Str(item, "versions"),
                        Mandatory = !item.TryGetProperty("optional", out var o) || o.ValueKind != JsonValueKind.True,
                    };
                }
            }
        }
    }

    // ==================== Quilt ====================

    private static MetaResult? ReadQuilt(ZipArchive archive)
    {
        var entry = FindEntry(archive, "quilt.mod.json");
        if (entry is null)
            return null;

        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("quilt_loader", out var ql) || ql.ValueKind != JsonValueKind.Object)
            return null;

        var id = Str(ql, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var name = id;
        if (ql.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            var n = Str(meta, "name");
            if (!string.IsNullOrWhiteSpace(n))
                name = n;
        }

        var deps = new List<ModDependency>();
        if (ql.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in depends.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    deps.Add(new ModDependency { Id = item.GetString() ?? string.Empty });
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    deps.Add(new ModDependency
                    {
                        Id = Str(item, "id"),
                        VersionRange = Str(item, "versions"),
                        Mandatory = !item.TryGetProperty("optional", out var o) || o.ValueKind != JsonValueKind.True,
                    });
                }
            }
        }

        return new MetaResult(id, name, Str(ql, "version"), "quilt.mod.json", ModLoaderKind.Quilt, deps);
    }

    // ==================== Forge / NeoForge（TOML） ====================

    private static MetaResult? ReadToml(ZipArchive archive)
    {
        var entry = FindEntry(archive, "META-INF/neoforge.mods.toml")
                    ?? FindEntry(archive, "META-INF/mods.toml");
        if (entry is null)
            return null;

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var toml = reader.ReadToEnd();

        var (mods, depsByOwner) = ParseToml(toml);

        var first = mods.FirstOrDefault();
        if (first is null || string.IsNullOrWhiteSpace(first.ModId))
            return null;

        var deps = depsByOwner.TryGetValue(first.ModId, out var list)
            ? list
            : new List<ModDependency>();

        // 同一个入口读两种 TOML：neoforge.mods.toml 记 NeoForge，mods.toml 记 Forge。
        // 这条区分是「Forge 实例里混进 Fabric 模组」类误报的基础 —— 必须先知道每个 jar 到底声明了谁。
        var loaderKind = entry.FullName.Contains("neoforge", StringComparison.OrdinalIgnoreCase)
            ? ModLoaderKind.NeoForge
            : ModLoaderKind.Forge;

        return new MetaResult(first.ModId, first.Name, first.Version,
            entry.FullName, loaderKind, deps);
    }

    /// <summary>
    /// 极简 TOML 扫描：只认 <c>[[mods]]</c>、<c>[[dependencies.&lt;ownerId&gt;]]</c> 两种表头
    /// 和其中形如 <c>key = "value"</c> / <c>key = true</c> 的键值。
    ///
    /// <para>
    /// 不引入完整 TOML 库是因为这里要的字段极其固定，而 mods.toml 由 Forge 的打包工具生成，
    /// 结构高度规整（表头 + 简单键值，没有嵌套表、没有数组）。真遇到写得出格的文件，
    /// 解析不出来只会表现为「这个模组参与不了体检」，不会给出错误结论。
    /// </para>
    /// </summary>
    private static (List<ModEntry> Mods, Dictionary<string, List<ModDependency>> Deps) ParseToml(string toml)
    {
        var mods = new List<ModEntry>();
        var deps = new Dictionary<string, List<ModDependency>>(StringComparer.OrdinalIgnoreCase);

        string? section = null;          // "mods" | "dependencies.<owner>"
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentOwner = string.Empty;

        void Flush()
        {
            if (section is null)
                return;

            if (section.Equals("mods", StringComparison.OrdinalIgnoreCase))
            {
                var id = Get(current, "modId");
                if (!string.IsNullOrWhiteSpace(id))
                    mods.Add(new ModEntry(id, Get(current, "displayName"), Get(current, "version")));
            }
            else if (section.StartsWith("dependencies", StringComparison.OrdinalIgnoreCase))
            {
                var depId = Get(current, "modId");
                if (!string.IsNullOrWhiteSpace(depId))
                {
                    var mandatory = !current.TryGetValue("mandatory", out var m)
                                    || m.Equals("true", StringComparison.OrdinalIgnoreCase);
                    var side = Get(current, "side");
                    // side=SERVER 的依赖是服务端要求，客户端实例里不该报缺失
                    var clientSide = string.IsNullOrWhiteSpace(side)
                                     || side.Equals("BOTH", StringComparison.OrdinalIgnoreCase)
                                     || side.Equals("CLIENT", StringComparison.OrdinalIgnoreCase);

                    var owner = string.IsNullOrWhiteSpace(currentOwner) ? "*" : currentOwner;
                    if (!deps.TryGetValue(owner, out var list))
                        deps[owner] = list = new List<ModDependency>();

                    list.Add(new ModDependency
                    {
                        Id = depId,
                        VersionRange = Get(current, "versionRange"),
                        Mandatory = mandatory,
                        ClientSide = clientSide,
                    });
                }
            }

            current.Clear();
        }

        foreach (var rawLine in toml.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("[[", StringComparison.Ordinal))
            {
                Flush();
                var name = line.Trim('[', ']', ' ', '\t', '\r');
                if (name.StartsWith("dependencies.", StringComparison.OrdinalIgnoreCase))
                {
                    section = "dependencies";
                    currentOwner = name["dependencies.".Length..].Trim().Trim('"', '\'');
                }
                else
                {
                    section = name;
                    currentOwner = string.Empty;
                }
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                // 单层表头（[mods.xxx] 之类）我们没有要读的字段，直接跳过
                Flush();
                section = null;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim().Trim('"', '\'');
            var value = Unquote(line[(eq + 1)..].Trim());
            if (key.Length > 0)
                current[key] = value;
        }

        Flush();
        return (mods, deps);
    }

    /// <summary>去掉行内注释：只有不在引号里的 <c>#</c> 才算注释起点。</summary>
    private static string StripComment(string line)
    {
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble) return line[..i];
        }

        return line;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];

        return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    // ==================== 小工具 ====================

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name)
    {
        foreach (var e in archive.Entries)
        {
            var n = e.FullName.Trim('/').Replace('\\', '/');
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    private static string Str(JsonElement obj, string prop)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(prop, out var el)
           && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Fabric 的 depends 值可能是字符串，也可能是对象（新 schema 的 <c>{ "versions": "..." }</c>）。</summary>
    private static string AsString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Object => Str(el, "versions"),
        _ => string.Empty,
    };

    private static string Get(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out var v) ? v : string.Empty;

    internal static string NormalizeId(string id)
        => new string((id ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string TryComputeSha1(FileInfo file)
    {
        try
        {
            using var stream = file.OpenRead();
            using var sha = SHA1.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private readonly record struct MetaResult(
        string ModId, string Name, string Version, string Source,
        ModLoaderKind Loader, List<ModDependency> Dependencies);

    /// <summary>mods.toml 里 <c>[[mods]]</c> 一条记录。用引用类型是为了让 FirstOrDefault 能返回 null
    /// （值类型的 FirstOrDefault 会静默给出全默认值，把「没有 mods 表」伪装成「有一个空 id 的模组」）。</summary>
    private sealed record ModEntry(string ModId, string Name, string Version);
}
