using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services;

/// <summary>一条「缺失的依赖」。</summary>
public sealed class MissingDependency
{
    public string ModId { get; init; } = string.Empty;
    public string VersionRange { get; init; } = string.Empty;

    /// <summary>谁在要它（展示成「被 A、B 需要」）。</summary>
    public List<string> RequiredBy { get; init; } = new();

    /// <summary>人类可读的一行。</summary>
    public string Describe() =>
        RequiredBy.Count == 0
            ? ModId
            : $"{ModId}（被 {string.Join("、", RequiredBy.Take(3))}{(RequiredBy.Count > 3 ? " 等" : "")} 需要）";
}

/// <summary>同一个模组 id 装了多份（版本冲突）。</summary>
public sealed class ModConflict
{
    public string ModId { get; init; } = string.Empty;
    public List<InstalledMod> Files { get; init; } = new();
}

/// <summary>一个实例的模组体检报告。</summary>
public sealed class ModHealthReport
{
    public string InstanceId { get; init; } = string.Empty;
    public ModLoaderKind Loader { get; init; } = ModLoaderKind.Unknown;
    public string GameVersion { get; init; } = string.Empty;
    public string ModsFolder { get; init; } = string.Empty;

    public List<InstalledMod> Installed { get; init; } = new();
    public List<MissingDependency> Missing { get; init; } = new();
    public List<ModConflict> Conflicts { get; init; } = new();

    /// <summary>
    /// 目录里读不出模组元数据的 jar（不是模组 / 损坏 / 未打包元数据）。
    /// </summary>
    public List<string> Unrecognized { get; init; } = new();

    /// <summary>
    /// 加载器与实例不匹配的模组（例如 Forge 实例 mods 里的 Fabric 模组）。
    ///
    /// <para>
    /// 这些 jar **不参与**依赖 / 冲突计算：游戏启动时根本不会加载它们，
    /// 拿它们算出来的「缺 fabric-api」「两个同名模组冲突」全是噪声。
    /// 以前就是这样误报的（用户反馈：Forge 实例体检出 Fabric 模组的问题）。
    /// </para>
    /// </summary>
    public List<InstalledMod> Incompatible { get; init; } = new();

    public List<string> Warnings { get; init; } = new();

    /// <summary>体检结论是否健康（没缺依赖、没冲突、没有放错加载器的模组）。</summary>
    public bool IsHealthy => Missing.Count == 0 && Conflicts.Count == 0 && Incompatible.Count == 0;

    /// <summary>一句话摘要，给通知 / 标题用。</summary>
    public string Summary()
    {
        if (Installed.Count == 0 && Unrecognized.Count == 0)
            return "这个实例还没有装任何模组";

        if (IsHealthy)
            return $"体检通过：{Installed.Count} 个模组，依赖完整";

        var parts = new List<string>();
        if (Missing.Count > 0) parts.Add($"缺 {Missing.Count} 个依赖");
        if (Conflicts.Count > 0) parts.Add($"{Conflicts.Count} 处重复/冲突");
        if (Incompatible.Count > 0) parts.Add($"{Incompatible.Count} 个模组加载器不匹配");
        return $"{Installed.Count} 个模组，" + string.Join("，", parts);
    }
}

/// <summary>「缺失依赖」对应的、可以从 Modrinth 补全的一个候选文件。</summary>
public sealed class DependencyCandidate
{
    public string ModId { get; init; } = string.Empty;
    public string RequiredBy { get; init; } = string.Empty;

    /// <summary>找到的 Modrinth 项目（null 表示没找到，只能手动处理）。</summary>
    public string Slug { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string VersionNumber { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Sha1 { get; init; } = string.Empty;
    public long Size { get; init; }

    /// <summary>没找到可下载文件时的原因（给用户看）。</summary>
    public string? FailureReason { get; init; }

    public bool HasDownload => !string.IsNullOrWhiteSpace(Url);
}

/// <summary>
/// 模组依赖求解器。
///
/// <para>
/// <b>做什么</b>：扫描实例的 <c>mods/</c>，读每个 jar 的加载器元数据，构建「谁提供了什么 id」
/// 与「谁需要什么 id」两张表，然后：
/// <list type="number">
/// <item>找出**必需但没有任何人提供**的依赖（缺依赖 —— 这就是「装完打不开、报一堆 mod 缺失」的根因）</item>
/// <item>找出**同一个 id 装了多份**（重复 / 版本冲突）</item>
/// </list>
/// </para>
///
/// <para>
/// <b>怎么补</b>：对每个缺失依赖，用 mod id 去 Modrinth 搜项目 → 拉它在「本实例加载器 + 游戏版本」
/// 下可用的版本 → 取主文件。拿不到就如实说明原因（常见于：这是个纯服务端库、或作者没发到 Modrinth）。
/// </para>
///
/// <para>
/// <b>不抛异常</b>：全部失败路径都转成报告里的条目。解析 / 下载都是 IO + 网络，必须能容忍单点失败。
/// </para>
/// </summary>
public static class ModDependencyResolver
{
    /// <summary>对一个实例做完整体检（只读本地，不联网）。</summary>
    public static ModHealthReport Analyze(string instanceId)
    {
        var modsFolder = GameInstanceHelper.ResolveInstanceFolder(instanceId, "mods");
        var loader = GameInstanceHelper.DetectLoader(
            Path.Combine(GameInstanceHelper.GetMinecraftFolder(), "versions"), instanceId);

        var all = ModMetadataReader.ReadAll(modsFolder);

        // 按加载器分区：Forge 实例 mods 目录里放着的 Fabric 模组，游戏启动时压根不会加载它们。
        // 拿它们去算「缺依赖 / 冲突」只会得到毫无意义的结论（Fabric 模组当然找不到 fabric-api，
        // 也不该和同名的 Forge 版互相算冲突）—— 这就是「Forge 版本体检出 Fabric 模组」的来源。
        var compatible = new List<InstalledMod>(all.Count);
        var incompatible = new List<InstalledMod>();
        foreach (var mod in all)
            (IsLoaderCompatible(loader, mod) ? compatible : incompatible).Add(mod);

        var unrecognized = new List<string>();
        try
        {
            if (Directory.Exists(modsFolder))
            {
                var known = new HashSet<string>(all.Select(m => m.FilePath), StringComparer.OrdinalIgnoreCase);
                foreach (var jar in Directory.GetFiles(modsFolder, "*.jar"))
                    if (!known.Contains(jar))
                        unrecognized.Add(Path.GetFileName(jar));
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "枚举未识别 jar 失败：{Dir}", modsFolder);
        }

        // 提供者表：所有已装**且能在本实例加载**的模组 id（归一化后）+ 它们自带的嵌套模组
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in compatible)
        {
            providers.Add(ModMetadataReader.NormalizeId(m.ModId));

            // jar-in-jar：Fabric API 那种「一个 jar 里装着几十个模块」的包，
            // 里面的模块 id 在运行时同样算「已提供」。不把它们算进来，
            // 装了 Fabric API 还会被报「缺 fabric-rendering-v1」——而那玩意在 Modrinth 上
            // 根本是独立项目，玩家点「一键补齐」也补不出正确结果。
            foreach (var nested in m.NestedIds)
                providers.Add(ModMetadataReader.NormalizeId(nested));
        }

        // 依赖需求表
        var needed = new Dictionary<string, MissingDependency>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in compatible)
        {
            foreach (var dep in mod.Dependencies)
            {
                if (!dep.Mandatory || !dep.ClientSide)
                    continue;

                var normId = ModMetadataReader.NormalizeId(dep.Id);
                if (normId.Length == 0)
                    continue;

                // 内置提供者（minecraft / java / 加载器本体 / 语言提供者）、
                // 或已被某个已装模组（含其嵌套 jar）提供 → 不缺
                if (ModMetadataReader.IsBuiltIn(dep.Id, loader) || providers.Contains(normId))
                    continue;

                if (needed.TryGetValue(normId, out var existing))
                    existing.RequiredBy.Add(mod.Name);
                else
                    needed[normId] = new MissingDependency
                    {
                        ModId = dep.Id,
                        VersionRange = dep.VersionRange,
                        RequiredBy = new List<string> { mod.Name },
                    };
            }
        }

        // 冲突：同一 id 出现在多个 jar 里（只看能加载的那些）
        var conflicts = compatible
            .GroupBy(m => ModMetadataReader.NormalizeId(m.ModId), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Count() > 1)
            .Select(g => new ModConflict { ModId = g.First().ModId, Files = g.ToList() })
            .ToList();

        var report = new ModHealthReport
        {
            InstanceId = instanceId,
            Loader = loader,
            GameVersion = TryGetGameVersion(instanceId),
            ModsFolder = modsFolder,
            Installed = all,
            Incompatible = incompatible,
            Missing = needed.Values.OrderBy(m => m.ModId, StringComparer.OrdinalIgnoreCase).ToList(),
            Conflicts = conflicts,
            Unrecognized = unrecognized,
        };

        if (loader is ModLoaderKind.Vanilla)
            report.Warnings.Add("这个实例是原版（没装模组加载器），装进来的模组不会生效");

        if (incompatible.Count > 0)
        {
            var groups = incompatible
                .GroupBy(m => m.Loader)
                .Select(g => $"{g.Count()} 个 {LoaderText(g.Key)}")
                .ToList();

            report.Warnings.Add(
                $"发现 {string.Join("、", groups)} 模组，与这个 {LoaderText(loader)} 实例不匹配，已排除在依赖检查之外 —— "
                + "它们不会生效，建议删掉或换成对应加载器的版本");
        }

        Serilog.Log.Information(
            "模组体检：实例 {Id}（{Loader}），{Count} 个模组（{Compatible} 个可加载，{Incompatible} 个加载器不匹配），缺 {Missing} 个依赖，{Conflict} 处重复",
            instanceId, report.Loader, all.Count, compatible.Count, incompatible.Count,
            report.Missing.Count, conflicts.Count);

        return report;
    }

    /// <summary>
    /// 判断一个模组能否在指定加载器的实例里被加载。
    ///
    /// <para>
    /// 判定依据是 jar 里**声明了哪些**加载器（<see cref="InstalledMod.Loaders"/>），不是文件名、
    /// 也不是它的依赖长什么样 —— 双声明（同时带 fabric.mod.json 与 mods.toml）的 jar 两边的实例都认。
    /// </para>
    ///
    /// <para>
    /// 宽容处理：Quilt 能加载 Fabric 模组；NeoForge 早期版本（1.20.1）能加载 Forge 模组；
    /// 原版 / 认不出加载器时不做过滤。宁可多算一个，也不要把实际能用的模组判成「不匹配」。
    /// </para>
    /// </summary>
    public static bool IsLoaderCompatible(ModLoaderKind instanceLoader, InstalledMod mod)
    {
        var loaders = mod.Loaders.Count > 0
            ? mod.Loaders
            : new List<ModLoaderKind> { mod.Loader };

        return instanceLoader switch
        {
            ModLoaderKind.Vanilla => true,
            ModLoaderKind.Unknown => true,
            ModLoaderKind.Fabric => loaders.Contains(ModLoaderKind.Fabric),
            ModLoaderKind.Quilt => loaders.Contains(ModLoaderKind.Quilt) || loaders.Contains(ModLoaderKind.Fabric),
            ModLoaderKind.Forge => loaders.Contains(ModLoaderKind.Forge),
            ModLoaderKind.NeoForge => loaders.Contains(ModLoaderKind.NeoForge) || loaders.Contains(ModLoaderKind.Forge),
            ModLoaderKind.LiteLoader => loaders.Contains(ModLoaderKind.LiteLoader),
            _ => true,
        };
    }

    /// <summary>加载器名称（给界面拼文案用）。</summary>
    public static string LoaderText(ModLoaderKind kind) => kind switch
    {
        ModLoaderKind.Fabric => "Fabric",
        ModLoaderKind.Quilt => "Quilt",
        ModLoaderKind.Forge => "Forge",
        ModLoaderKind.NeoForge => "NeoForge",
        ModLoaderKind.LiteLoader => "LiteLoader",
        ModLoaderKind.Vanilla => "原版",
        _ => "未知加载器",
    };

    /// <summary>
    /// 给一个缺失依赖找一个可下载的文件。找不到返回一个带 <see cref="DependencyCandidate.FailureReason"/> 的对象。
    /// </summary>
    public static async Task<DependencyCandidate> ResolveAsync(
        MissingDependency missing,
        ModLoaderKind loader,
        string? gameVersion,
        CancellationToken ct = default)
    {
        var requiredBy = string.Join("、", missing.RequiredBy.Take(3));

        string? loaderTag = loader switch
        {
            ModLoaderKind.Fabric => "fabric",
            ModLoaderKind.Quilt => "quilt",
            ModLoaderKind.Forge => "forge",
            ModLoaderKind.NeoForge => "neoforge",
            _ => null,
        };

        try
        {
            var project = await ModrinthApi.FindProjectByModIdAsync(missing.ModId, loaderTag, gameVersion, ct);
            if (project is null)
                return Fail(missing, requiredBy, "在 Modrinth 上没有找到这个模组（可能是自制库或纯服务端模组），请手动安装");

            var versions = await ModrinthApi.GetProjectVersionsAsync(project.Slug, loaderTag, gameVersion, ct);
            if (versions.Count == 0)
            {
                // 退一步：不筛加载器 / 版本再试一次，至少告诉用户项目存在
                versions = await ModrinthApi.GetProjectVersionsAsync(project.Slug, null, null, ct);
                if (versions.Count == 0)
                    return Fail(missing, requiredBy, $"Modrinth 上「{project.Title}」没有可用版本");
            }

            var version = versions.FirstOrDefault(v =>
                              string.IsNullOrWhiteSpace(gameVersion) || v.GameVersions.Contains(gameVersion))
                          ?? versions.First();

            var file = version.PrimaryFile;
            if (file is null || string.IsNullOrWhiteSpace(file.Url))
                return Fail(missing, requiredBy, $"「{project.Title}」的版本里没有可下载文件");

            return new DependencyCandidate
            {
                ModId = missing.ModId,
                RequiredBy = requiredBy,
                Slug = project.Slug,
                Title = project.Title,
                VersionNumber = version.VersionNumber,
                FileName = string.IsNullOrWhiteSpace(file.FileName) ? $"{project.Slug}.jar" : file.FileName,
                Url = file.Url,
                Sha1 = file.Sha1,
                Size = file.Size,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "解析依赖失败：{Id}", missing.ModId);
            return Fail(missing, requiredBy, "查询 Modrinth 时出错：" + ex.Message);
        }
    }

    /// <summary>批量解析（顺序执行，避免触发 Modrinth 限流）。</summary>
    public static async Task<List<DependencyCandidate>> ResolveAllAsync(
        ModHealthReport report,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        var result = new List<DependencyCandidate>();
        var total = report.Missing.Count;

        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var missing = report.Missing[i];
            onProgress?.Invoke(i + 1, total, missing.ModId);
            result.Add(await ResolveAsync(missing, report.Loader, report.GameVersion, ct));
        }

        return result;
    }

    private static DependencyCandidate Fail(MissingDependency missing, string requiredBy, string reason)
        => new()
        {
            ModId = missing.ModId,
            RequiredBy = requiredBy,
            FailureReason = reason,
        };

    /// <summary>
    /// 取实例对应的 Minecraft 版本：自己的版本 JSON 有 <c>inheritsFrom</c> 就用它，
    /// 否则实例 id 本身就是版本号（原版实例）。
    /// </summary>
    public static string TryGetGameVersion(string instanceId)
    {
        try
        {
            var json = Path.Combine(
                GameInstanceHelper.GetMinecraftFolder(), "versions", instanceId, instanceId + ".json");
            if (!File.Exists(json))
                return string.Empty;

            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            if (doc.RootElement.TryGetProperty("inheritsFrom", out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                var parent = el.GetString();
                if (!string.IsNullOrWhiteSpace(parent))
                    return parent!;
            }
        }
        catch
        {
            // 读不出就当没有版本信息，只是少一层筛选而已
        }

        return instanceId;
    }

    /// <summary>
    /// 从版本范围里拿一个「参考版本号」，用于给用户一句「需要 ≥1.2.0」这样的提示。
    /// 不追求完整语义解析 —— 范围语法各家不一，只提取第一个数字串。
    /// </summary>
    public static string? ExtractReferenceVersion(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
            return null;

        var m = Regex.Match(range, @"\d+(\.\d+)*");
        return m.Success ? m.Value : null;
    }
}
