using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VibrantbitLauncher.Services;

/// <summary>缺失资源的种类。用于在「补全资源」窗口里给出可读的分类标签。</summary>
public enum IntegrityIssueKind
{
    /// <summary>客户端主 jar（versions/&lt;id&gt;/&lt;id&gt;.jar）。</summary>
    ClientJar,

    /// <summary>资源索引（assets/indexes/&lt;id&gt;.json）。</summary>
    AssetIndex,

    /// <summary>依赖库（libraries/...）。</summary>
    Library,

    /// <summary>资源对象（assets/objects/xx/&lt;hash&gt;）。</summary>
    AssetObject,
}

/// <summary>一条缺失记录。同类会先聚合成一条，避免几千个资源对象把列表刷爆。</summary>
public sealed class IntegrityIssue
{
    public IntegrityIssueKind Kind { get; init; }

    /// <summary>标题，例如「客户端文件缺失」。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>说明，列文件名 / 数量 / 示例。</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>缺失数量（资源对象会是一个较大的数字）。</summary>
    public int Count { get; init; } = 1;

    /// <summary>分类标签：客户端 / 资源索引 / 依赖库 / 资源文件。</summary>
    public string KindText => Kind switch
    {
        IntegrityIssueKind.ClientJar => "客户端",
        IntegrityIssueKind.AssetIndex => "资源索引",
        IntegrityIssueKind.Library => "依赖库",
        IntegrityIssueKind.AssetObject => "资源文件",
        _ => "其他",
    };
}

/// <summary>一次启动前体检的结果。</summary>
public sealed class IntegrityReport
{
    public string McVersion { get; init; } = string.Empty;

    /// <summary>补全时真正要修的**原版**版本号（沿 inheritsFrom 一路走到底）。</summary>
    public string RootVersion { get; init; } = string.Empty;

    public string McFolder { get; init; } = string.Empty;

    public List<IntegrityIssue> Issues { get; } = new();

    public bool IsHealthy => Issues.Count == 0;

    /// <summary>一句话摘要，例如「缺少 3 类资源（客户端文件、依赖库、资源文件）」。</summary>
    public string Summary()
    {
        if (IsHealthy)
            return "资源完整";

        var kinds = string.Join("、", Issues.Select(i => i.KindText).Distinct());
        return $"检测到 {Issues.Count} 类资源缺失（{kinds}）";
    }
}

/// <summary>
/// 启动前的实例完整性体检：client.jar / 资源索引 / 依赖库 / 资源对象。
///
/// <para>
/// 为什么要有它：这几项缺一个游戏就起不来（或起来后卡在加载界面），
/// 而原来的做法是**在启动流程里直接抛异常**、把一句很长的中文错误弹给用户，
/// 让用户自己去「下载中心」重装 —— 但重装整个版本要下几百 MB，
/// 实际只缺几个文件。这里把「缺什么」整理成一份清单，
/// 交给 <see cref="AssetRepairService"/> 只补缺的那部分。
/// </para>
///
/// <para>全部是本地文件 IO，不联网；调用方应放到线程池执行（几百上千次 File.Exists）。</para>
/// </summary>
public static class InstanceIntegrityChecker
{
    /// <summary>
    /// 客户端 jar 的体积下限，只用来挡住空文件 / 几百字节的错误页。
    ///
    /// <para>
    /// ⚠️ 这个值**不能按现代版本的体积来定**。老版本的客户端本体非常小：
    /// <c>rd-132211</c>（2009 年第一个公开版本）只有 26 KB，<c>c0.30_01c</c> 约 0.3 MB。
    /// 1.0.34.0 之前这里写的是 512 KB，于是所有 2009 年的 Classic 版本都会被判成
    /// 「客户端文件损坏」，而文件其实完好无损。
    /// </para>
    ///
    /// <para>
    /// 真正能判「被截断」的是结构而不是体积，见 <see cref="LooksLikeValidJar"/>。
    /// </para>
    /// </summary>
    private const long MinClientJarSize = 1024;

    /// <summary>zip 结尾记录（EOCD）的签名长度，用于判断压缩包是否写完整。</summary>
    private const int EocdSignatureLength = 4;

    /// <summary>EOCD 搜索窗口：注释最长 65535 字节，多给一点余量。</summary>
    private const int EocdSearchWindow = 66 * 1024;

    /// <summary>体检入口。</summary>
    public static IntegrityReport Check(string mcFolder, string versionId)
    {
        var report = new IntegrityReport
        {
            McVersion = versionId,
            McFolder = mcFolder,
            RootVersion = ResolveRootVersion(mcFolder, versionId) ?? versionId,
        };

        var root = report.RootVersion;

        // ── ① 客户端 jar ────────────────────────────────────────────────
        // 加载器实例（Forge/Fabric…）的 jar 在父版本目录里，所以这里查的是 root。
        var clientJar = Path.Combine(mcFolder, "versions", root, root + ".jar");
        if (!File.Exists(clientJar))
        {
            report.Issues.Add(new IntegrityIssue
            {
                Kind = IntegrityIssueKind.ClientJar,
                Title = "客户端文件缺失",
                Detail = $"{root}.jar 不存在（{clientJar}）",
            });
        }
        else if (!LooksLikeValidJar(clientJar, out var reason))
        {
            report.Issues.Add(new IntegrityIssue
            {
                Kind = IntegrityIssueKind.ClientJar,
                Title = "客户端文件损坏",
                Detail = $"{root}.jar {reason}（多半是下载被限速 / 代理截断，重新下载即可）",
            });
        }

        // ── ② 资源索引 + ③ 资源对象 ──────────────────────────────────────
        var assetsId = ResolveAssetsId(mcFolder, versionId);
        if (!string.IsNullOrWhiteSpace(assetsId))
        {
            var indexPath = Path.Combine(mcFolder, "assets", "indexes", assetsId + ".json");
            if (!File.Exists(indexPath))
            {
                report.Issues.Add(new IntegrityIssue
                {
                    Kind = IntegrityIssueKind.AssetIndex,
                    Title = "资源索引缺失",
                    Detail = $"assets/indexes/{assetsId}.json 不存在（缺了它游戏会在加载资源阶段直接退出）",
                });
            }
            else
            {
                var missing = CountMissingAssetObjects(mcFolder, indexPath, assetsId, versionId);
                if (missing > 0)
                {
                    report.Issues.Add(new IntegrityIssue
                    {
                        Kind = IntegrityIssueKind.AssetObject,
                        Title = "资源文件缺失",
                        Detail = $"assets/objects 下缺 {missing} 个资源文件（音效 / 语言 / 贴图等）",
                        Count = missing,
                    });
                }
            }
        }

        // ── ④ 依赖库 ────────────────────────────────────────────────────
        var missingLibs = CollectMissingLibraries(mcFolder, versionId);
        if (missingLibs.Count > 0)
        {
            var sample = string.Join("、", missingLibs.Take(6).Select(Path.GetFileName));
            var more = missingLibs.Count > 6 ? $" 等 {missingLibs.Count} 个" : string.Empty;
            report.Issues.Add(new IntegrityIssue
            {
                Kind = IntegrityIssueKind.Library,
                Title = "依赖库缺失",
                Detail = $"缺 {missingLibs.Count} 个库文件：{sample}{more}",
                Count = missingLibs.Count,
            });
        }

        return report;
    }

    /// <summary>沿 inheritsFrom 一路往上，返回最顶层的原版版本号（找不到时返回传入的 id）。</summary>
    public static string? ResolveRootVersion(string mcFolder, string versionId)
    {
        var current = versionId;
        for (var guard = 0; guard < 16; guard++)
        {
            var parent = ReadInheritsFrom(mcFolder, current);
            if (string.IsNullOrWhiteSpace(parent))
                return current;

            current = parent;
        }

        return current;
    }

    /// <summary>读取版本 JSON 里的 assetIndex.id（旧版本用 assets 字段）。</summary>
    private static string? ResolveAssetsId(string mcFolder, string versionId)
    {
        // 自己没写就往父版本找：加载器实例通常继承原版的 assets 字段。
        var current = versionId;
        for (var guard = 0; guard < 16; guard++)
        {
            var json = TryReadVersionJson(mcFolder, current);
            if (json is { } root)
            {
                if (root.TryGetProperty("assetIndex", out var ai)
                    && ai.TryGetProperty("id", out var idEl)
                    && idEl.GetString() is { Length: > 0 } id)
                {
                    return id;
                }

                if (root.TryGetProperty("assets", out var assets)
                    && assets.GetString() is { Length: > 0 } legacy)
                {
                    return legacy;
                }
            }

            var parent = ReadInheritsFrom(mcFolder, current);
            if (string.IsNullOrWhiteSpace(parent))
                return null;

            current = parent;
        }

        return null;
    }

    private static string? ReadInheritsFrom(string mcFolder, string versionId)
    {
        var json = TryReadVersionJson(mcFolder, versionId);
        if (json is not { } root)
            return null;

        return root.TryGetProperty("inheritsFrom", out var inh) ? inh.GetString() : null;
    }

    private static JsonElement? TryReadVersionJson(string mcFolder, string versionId)
    {
        try
        {
            var path = Path.Combine(mcFolder, "versions", versionId, versionId + ".json");
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            // Clone：JsonDocument 释放后 Element 不能再用
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 统计资源索引里有多少个对象在本地缺失（只判断存在性，不校验哈希 —— 哈希校验交给补全时的库）。
    ///
    /// <para>
    /// 1.6 / 1.7 时代的资源索引带 <c>virtual</c> 或 <c>map_to_resources</c> 标记，游戏读的不是
    /// <c>assets/objects</c>，而是**解包出来的副本**（<c>assets/virtual/&lt;id&gt;/…</c> 或
    /// <c>resources/…</c>）。这类实例如果是从别的启动器搬过来的，可能只有副本、没有 objects，
    /// 游戏跑得好好的，按 objects 逐个查就会报出上千个「缺失」。所以副本存在就算有。
    /// </para>
    /// </summary>
    private static int CountMissingAssetObjects(
        string mcFolder, string indexPath, string assetsId, string versionId)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("objects", out var objects)
                || objects.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            var useVirtual = root.TryGetProperty("virtual", out var virtualEl)
                             && virtualEl.ValueKind == JsonValueKind.True;
            var useResources = root.TryGetProperty("map_to_resources", out var mapEl)
                               && mapEl.ValueKind == JsonValueKind.True;

            var virtualDir = Path.Combine(mcFolder, "assets", "virtual", assetsId);
            // 版本隔离开着时游戏读的是 versions/<id>/resources，两个位置都算
            var resourceDirs = new[]
            {
                Path.Combine(mcFolder, "resources"),
                Path.Combine(mcFolder, "versions", versionId, "resources"),
            };

            var missing = 0;
            foreach (var item in objects.EnumerateObject())
            {
                var hash = item.Value.TryGetProperty("hash", out var hashEl) ? hashEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
                    continue;

                var path = Path.Combine(mcFolder, "assets", "objects", hash[..2], hash);
                if (File.Exists(path))
                    continue;

                var relative = item.Name.Replace('/', Path.DirectorySeparatorChar);

                if (useVirtual && File.Exists(Path.Combine(virtualDir, relative)))
                    continue;

                if (useResources && resourceDirs.Any(dir => File.Exists(Path.Combine(dir, relative))))
                    continue;

                missing++;
            }

            return missing;
        }
        catch
        {
            // 索引本身是坏的 → 交给补全流程重下索引（这里不重复报错）
            return 0;
        }
    }

    /// <summary>按 inheritsFrom 链收集所有 libraries 里缺失的文件（跳过被规则排除的非 Windows 库）。</summary>
    private static List<string> CollectMissingLibraries(string mcFolder, string versionId)
    {
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var current = versionId;
        for (var guard = 0; guard < 16; guard++)
        {
            var json = TryReadVersionJson(mcFolder, current);
            if (json is { } root
                && root.TryGetProperty("libraries", out var libs)
                && libs.ValueKind == JsonValueKind.Array)
            {
                foreach (var lib in libs.EnumerateArray())
                {
                    if (!IsAllowedOnThisOs(lib))
                        continue;

                    var path = GetLibraryPath(lib, mcFolder);
                    if (path == null || !seen.Add(path))
                        continue;

                    if (!File.Exists(path))
                        missing.Add(path);
                }
            }

            var parent = ReadInheritsFrom(mcFolder, current);
            if (string.IsNullOrWhiteSpace(parent))
                break;

            current = parent;
        }

        return missing;
    }

    /// <summary>
    /// 极简 rules 判定：只有显式声明了 os 规则时才有意义。
    /// 没有 rules → 适用；有 rules → 最后一条匹配的 allow/disallow 决定。
    ///
    /// <para>
    /// ⚠️ 这里的「当前系统」**必须按实际平台判断**。1.0.30.x 之前它写死成 <c>"windows"</c>，
    /// 于是 Linux / macOS 上出现的是完全相反的结果：<c>natives-windows</c> 因为
    /// 「os.name 就是 windows，匹配成功」被留下，而 <c>natives-linux</c> 被判成「不是 windows」
    /// 全部剔除 —— 界面上就变成「Linux 机器缺一堆 Windows 依赖」。
    /// </para>
    /// </summary>
    internal static bool IsAllowedOnThisOs(JsonElement lib)
        => IsAllowedOnOs(lib, CurrentOsName, CurrentOsArch);

    /// <summary>
    /// 按给定平台做 rules 判定（拆出来是为了能在测试里用别的平台口径跑一遍 ——
    /// 这段逻辑一旦写错，Windows 上完全正常、只有 Linux / macOS 会错，最难发现）。
    /// </summary>
    internal static bool IsAllowedOnOs(JsonElement lib, string osName, Architecture osArch)
    {
        if (!lib.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return true;

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var matches = true;
            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("name", out var nameEl)
                    && nameEl.GetString() is { Length: > 0 } name
                    && !string.Equals(name, osName, StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                }

                // arch 只在显式给出且有对应处理时才判，避免把没见过的架构值当成「不匹配」。
                // x86 = 32 位、arm64 = ARM 64 位，其余（x86_64 / arm32 等）不参与判定。
                if (matches
                    && os.TryGetProperty("arch", out var archEl)
                    && archEl.GetString() is { Length: > 0 } arch)
                {
                    if (string.Equals(arch, "x86", StringComparison.OrdinalIgnoreCase))
                        matches = osArch == Architecture.X86;
                    else if (string.Equals(arch, "arm64", StringComparison.OrdinalIgnoreCase))
                        matches = osArch == Architecture.Arm64;
                }
            }

            if (matches)
            {
                var action = rule.TryGetProperty("action", out var actEl) ? actEl.GetString() : "allow";
                allowed = !string.Equals(action, "disallow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }

    /// <summary>当前平台在 Minecraft 版本 JSON 里的写法：<c>windows</c> / <c>linux</c> / <c>osx</c>。</summary>
    private static string CurrentOsName =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "osx"
        : "linux";

    /// <summary>当前进程的 CPU 架构（用于 <c>os.arch</c> 规则的 x86 / arm64 判定）。</summary>
    private static Architecture CurrentOsArch
    {
        get
        {
            try
            {
                return RuntimeInformation.ProcessArchitecture;
            }
            catch
            {
                return Architecture.X64;
            }
        }
    }

    /// <summary>
    /// 与启动流程同口径的库路径解析：
    /// <c>downloads.artifact.path</c> → <c>downloads.classifiers[natives[本平台]]</c> → 按 maven 坐标拼。
    ///
    /// <para>
    /// ⚠️ 中间那一步不能省。1.13 之前（LWJGL2 时代）的 <c>lwjgl-platform</c> /
    /// <c>jinput-platform</c> / <c>twitch-platform</c> 在 JSON 里**只有 classifiers、没有 artifact**，
    /// 本地真实文件名是 <c>xxx-&lt;ver&gt;-natives-windows.jar</c>。漏掉这一步就会退化成按
    /// <c>name</c> 拼出 <c>xxx-&lt;ver&gt;.jar</c> —— 那个文件从头到尾都不存在，于是**每个**
    /// 老版本都会报「依赖库缺失 2~4 个」，补全也补不回来（下载器同样只会下分类器）。
    /// </para>
    ///
    /// <para>
    /// 返回 <c>null</c> 表示「这一项在本平台不需要 / 无法定位」，调用方直接跳过、不报缺失。
    /// </para>
    /// </summary>
    internal static string? GetLibraryPath(JsonElement lib, string mcFolder)
    {
        if (lib.TryGetProperty("downloads", out var dl) && dl.ValueKind == JsonValueKind.Object)
        {
            // ① 普通制品：直接进 classpath 的那个 jar
            if (dl.TryGetProperty("artifact", out var art)
                && art.TryGetProperty("path", out var pathEl)
                && pathEl.GetString() is { Length: > 0 } rel)
            {
                return Path.Combine(mcFolder, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
            }

            // ② 只有分类器：按 natives 映射取本平台那一个
            if (dl.TryGetProperty("classifiers", out var classifiers)
                && classifiers.ValueKind == JsonValueKind.Object)
            {
                var key = ResolveNativesClassifierKey(lib);
                if (key is null)
                    return null;

                if (!classifiers.TryGetProperty(key, out var cl)
                    || cl.GetPropertyOrDefault("path") is not { Length: > 0 } clRel)
                {
                    // 声明了 natives，但分类器表里没有这个键 —— 启动时也取不到，不该算缺失
                    return null;
                }

                return Path.Combine(mcFolder, "libraries", clRel.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        // ③ 老 Forge / 早期加载器：库声明里连 downloads 都没有，只能按 maven 坐标拼
        if (lib.TryGetProperty("name", out var nameEl)
            && nameEl.GetString() is { Length: > 0 } name)
        {
            return MavenPath(mcFolder, name);
        }

        return null;
    }

    /// <summary>
    /// 取这个库在本平台的 **natives 压缩包**路径（要解压到 natives 目录的那个 jar）。
    ///
    /// <para>
    /// 与 <see cref="GetLibraryPath"/> 的区别：后者回答「体检时该找哪个文件」，
    /// 可能是普通 artifact、也可能是 natives 分类器；这里**只认分类器**，
    /// 因为启动链路需要拿它解压 dll / so / dylib。
    /// </para>
    ///
    /// <para>
    /// 老 Forge 那种连 <c>downloads</c> 都没有的库、以及没声明 <c>natives</c> 的库，都返回
    /// <c>null</c> —— 它们本来就没有原生库要解压。
    /// </para>
    /// </summary>
    internal static string? GetNativesJarPath(JsonElement lib, string mcFolder)
    {
        if (!lib.TryGetProperty("downloads", out var dl) || dl.ValueKind != JsonValueKind.Object)
            return null;

        if (!dl.TryGetProperty("classifiers", out var classifiers)
            || classifiers.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var key = ResolveNativesClassifierKey(lib);
        if (key is null || !classifiers.TryGetProperty(key, out var classifier))
            return null;

        if (classifier.GetPropertyOrDefault("path") is not { Length: > 0 } rel)
            return null;

        return Path.Combine(mcFolder, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// 取 <c>extract.exclude</c> 里声明要跳过的条目前缀（LWJGL 系一律是 <c>META-INF/</c>）。
    /// 老版本的 natives 包里除了 dll 还塞了签名文件，原样解出来会被 Java 当成重复签名。
    /// </summary>
    internal static string[] GetNativesExclude(JsonElement lib)
    {
        if (!lib.TryGetProperty("extract", out var extract) || extract.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();

        if (!extract.TryGetProperty("exclude", out var exclude) || exclude.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return exclude.EnumerateArray()
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray()!;
    }

    /// <summary>
    /// 按 maven 坐标拼本地路径，支持 <c>group:artifact:version[:classifier][@ext]</c>。
    ///
    /// <para>
    /// classifier 必须带上：老 Forge 的库写成 <c>net.minecraftforge:forge:1.7.10-10.13.4.1614:universal@jar</c>，
    /// 真实文件名是 <c>forge-1.7.10-10.13.4.1614-universal.jar</c>；忽略最后一段就会去找
    /// 一个不存在的 <c>forge-1.7.10-10.13.4.1614.jar</c>。
    /// </para>
    /// </summary>
    internal static string? MavenPath(string mcFolder, string name)
    {
        var at = name.IndexOf('@');
        if (at > 0)
            name = name[..at];

        var parts = name.Split(':');
        if (parts.Length < 3)
            return null;

        var group = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length >= 4 && parts[3].Length > 0 ? "-" + parts[3] : string.Empty;

        return Path.Combine(mcFolder, "libraries", group, artifact, version,
            $"{artifact}-{version}{classifier}.jar");
    }

    /// <summary>
    /// 从 <c>natives</c> 映射里取出本平台的分类器键（如 <c>natives-windows-64</c>）。
    ///
    /// <para>
    /// 值是模板，Windows 上可能是 <c>natives-windows-${arch}</c>，要把 <c>${arch}</c>
    /// 换成 <c>32</c> / <c>64</c>。没有本平台的键就返回 <c>null</c>（不需要这个库）。
    /// </para>
    /// </summary>
    private static string? ResolveNativesClassifierKey(JsonElement lib)
    {
        if (!lib.TryGetProperty("natives", out var natives) || natives.ValueKind != JsonValueKind.Object)
            return null;

        string? template = null;
        foreach (var entry in natives.EnumerateObject())
        {
            if (!string.Equals(entry.Name, CurrentOsName, StringComparison.OrdinalIgnoreCase))
                continue;

            template = entry.Value.GetString();
            break;
        }

        if (string.IsNullOrWhiteSpace(template))
            return null;

        return template.Replace("${arch}", CurrentOsArch == Architecture.X86 ? "32" : "64",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取一个可能缺失的字符串属性；没有就返回 null。</summary>
    private static string? GetPropertyOrDefault(this JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetString() : null;

    /// <summary>
    /// 判断客户端 jar 是不是一个**结构完整**的 zip。
    ///
    /// <para>
    /// 只看体积不行（见 <see cref="MinClientJarSize"/> 的注释），只看开头 4 字节也不行：
    /// 被截断的 jar 开头照样是 <c>PK\x03\x04</c>，只有写完整了才会在结尾带 EOCD 记录
    /// （<c>PK\x05\x06</c>）。两者都查，才能把「真的坏了」和「老版本本来就小」分开。
    /// </para>
    /// </summary>
    /// <param name="reason">不合法时给出给用户看的短句（如「结尾缺少目录结构」）。</param>
    internal static bool LooksLikeValidJar(string path, out string reason)
    {
        reason = string.Empty;

        try
        {
            using var fs = File.OpenRead(path);

            if (fs.Length < MinClientJarSize)
            {
                reason = $"只有 {fs.Length} 字节";
                return false;
            }

            var head = new byte[4];
            if (fs.Read(head, 0, 4) != 4 || head[0] != 0x50 || head[1] != 0x4B)
            {
                reason = "不是 jar 文件（开头不是 zip 结构）";
                return false;
            }

            // 在结尾一段里找 EOCD：写完整的 zip 一定有，截断的没有
            var window = (int)Math.Min(fs.Length, EocdSearchWindow);
            var tail = new byte[window];
            fs.Seek(-window, SeekOrigin.End);

            var read = 0;
            while (read < window)
            {
                var n = fs.Read(tail, read, window - read);
                if (n <= 0)
                    break;
                read += n;
            }

            for (var i = read - EocdSignatureLength; i >= 0; i--)
            {
                if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
                    return true;
            }

            reason = "结尾缺少 zip 目录结构（文件没下完）";
            return false;
        }
        catch (Exception ex)
        {
            reason = "无法读取（" + ex.GetType().Name + "）";
            return false;
        }
    }
}
