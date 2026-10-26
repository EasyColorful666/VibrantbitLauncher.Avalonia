using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>客户端 jar 的合理下限：正常十几 MB，小于这个基本是错误页 / 空文件。</summary>
    private const long MinClientJarSize = 512 * 1024;

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
        else
        {
            var size = new FileInfo(clientJar).Length;
            if (size < MinClientJarSize || !IsZipFile(clientJar))
            {
                report.Issues.Add(new IntegrityIssue
                {
                    Kind = IntegrityIssueKind.ClientJar,
                    Title = "客户端文件损坏",
                    Detail = $"{root}.jar 只有 {size} 字节，或不是有效的 jar（多半是被限速 / 代理截断）",
                });
            }
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
                var missing = CountMissingAssetObjects(mcFolder, indexPath);
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

    /// <summary>统计资源索引里有多少个对象在本地缺失（只判断存在性，不校验哈希 —— 哈希校验交给补全时的库）。</summary>
    private static int CountMissingAssetObjects(string mcFolder, string indexPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
            if (!doc.RootElement.TryGetProperty("objects", out var objects)
                || objects.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            var missing = 0;
            foreach (var item in objects.EnumerateObject())
            {
                var hash = item.Value.TryGetProperty("hash", out var hashEl) ? hashEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
                    continue;

                var path = Path.Combine(mcFolder, "assets", "objects", hash[..2], hash);
                if (!File.Exists(path))
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
    /// </summary>
    internal static bool IsAllowedOnThisOs(JsonElement lib)
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
                    && !string.Equals(name, "windows", StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                }
            }

            // 只处理 windows/未声明的情况，arch 差异（32/64 位）交给补全阶段，
            // 体检阶段宁可多报一个"缺失"，也不要漏报导致游戏起不来。
            if (matches)
            {
                var action = rule.TryGetProperty("action", out var actEl) ? actEl.GetString() : "allow";
                allowed = !string.Equals(action, "disallow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }

    /// <summary>与启动流程同口径的库路径解析：优先 downloads.artifact.path，退化按 maven 坐标拼。</summary>
    private static string? GetLibraryPath(JsonElement lib, string mcFolder)
    {
        if (lib.TryGetProperty("downloads", out var dl)
            && dl.TryGetProperty("artifact", out var art)
            && art.TryGetProperty("path", out var pathEl)
            && pathEl.GetString() is { Length: > 0 } rel)
        {
            return Path.Combine(mcFolder, "libraries", rel.Replace('/', Path.DirectorySeparatorChar));
        }

        if (lib.TryGetProperty("name", out var nameEl)
            && nameEl.GetString() is { Length: > 0 } name)
        {
            var parts = name.Split(':');
            if (parts.Length >= 3)
            {
                var group = parts[0].Replace('.', Path.DirectorySeparatorChar);
                return Path.Combine(mcFolder, "libraries", group, parts[1], parts[2], $"{parts[1]}-{parts[2]}.jar");
            }
        }

        return null;
    }

    /// <summary>前 4 字节是不是 ZIP 头（PK\x03\x04）——用来识别「文件在、内容是被拦截后的 HTML」。</summary>
    private static bool IsZipFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[4];
            return fs.Read(buf, 0, 4) == 4 && buf[0] == 0x50 && buf[1] == 0x4B;
        }
        catch
        {
            return false;
        }
    }
}
