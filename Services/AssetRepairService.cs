using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MinecraftLaunch.Base.Models.Network;
using MinecraftLaunch.Components.Downloader;
using MinecraftLaunch.Components.Parser;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 「补全资源」的执行体：只补缺的那部分，不重装整个版本。
///
/// <para>
/// 分两阶段，因为一个实例可能同时缺两类东西：
/// <list type="number">
/// <item><b>原版层</b>（client.jar / 原版依赖库 / 资源索引 / 资源对象）——
/// 直接复用 <see cref="VanillaDownloader.InstallAsync"/>：它会重下版本 JSON、
/// 规范化后交给库的 <c>MinecraftResourceDownloader</c> 做「校验 + 只下缺的」，
/// 那正是我们要的语义，而且超时 / 双源 / 中文报错都已经在里面了。</item>
/// <item><b>加载器层</b>（Forge / Fabric / Quilt 自己的 maven 制品）——
/// 对实例本身再跑一次 <c>VerifyAndDownloadDependenciesAsync</c>。失败只记日志：
/// 加载器库缺失不该让「资源补全」整体判死，用户至少能进原版。</item>
/// </list>
/// </para>
/// </summary>
public static class AssetRepairService
{
    /// <summary>原版阶段占进度条的比重（剩下的留给加载器库）。</summary>
    private const double VanillaWeight = 0.9;

    /// <summary>
    /// 执行补全。
    /// </summary>
    /// <param name="report">体检报告（提供 mcFolder / 实例版本 / 原版根版本）。</param>
    /// <param name="onProgress">进度回调 (百分比 0~100, 步骤文案, 速度文案)。</param>
    /// <param name="ct">取消令牌（只在阶段之间与每次回调前检查）。</param>
    /// <returns>一句给用户看的结果说明。</returns>
    public static async Task<string> RepairAsync(
        IntegrityReport report,
        Action<int, string, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (report == null)
            throw new ArgumentNullException(nameof(report));

        ct.ThrowIfCancellationRequested();
        onProgress?.Invoke(1, "准备补全资源", string.Empty);

        // ── 阶段 ①：原版层 ───────────────────────────────────────────
        var entry = await ResolveManifestEntryAsync(report.RootVersion, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        await VanillaDownloader.InstallAsync(report.McFolder, entry, (percent, step, speed) =>
        {
            if (ct.IsCancellationRequested)
                return;

            onProgress?.Invoke(
                (int)Math.Clamp(percent * VanillaWeight, 0, 90),
                step,
                speed);
        }, ct).ConfigureAwait(false);

        // ── 阶段 ②：加载器层（实例自己的库） ──────────────────────────
        var basePercent = (int)(VanillaWeight * 100);
        if (!string.Equals(report.McVersion, report.RootVersion, StringComparison.OrdinalIgnoreCase))
        {
            // 先看有没有「装不回来」的库，再决定要不要跑下载。
            //
            // Forge / NeoForge 的 forge-<mc>-<ver>-client.jar 是安装处理器用 binarypatcher
            // 打补丁**本地生成**的产物：install_profile 里它的 url 是空字符串，
            // maven 上也不存在（实测官方与 BMCLAPI 都是 404）。
            // 下载器遇到空 url 会自己拼 maven 地址，于是必然 404 / 超时，
            // 白等好几轮之后报一句「连接超时」，用户完全看不出真正原因。
            //
            // 这类缺口只有重装加载器能补，所以直接给出可操作的结论。
            var generated = FindProcessorGeneratedMissing(report.McFolder, report.McVersion);
            if (generated.Count > 0)
            {
                var names = string.Join("、", generated);
                Serilog.Log.Warning(
                    "补全资源：{Files} 是安装处理器生成的产物，尝试重跑加载器安装来重建（实例 {Version}）",
                    names, report.McVersion);

                onProgress?.Invoke(basePercent, "重新生成加载器补丁文件", string.Empty);

                if (await TryRebuildPatchedLibrariesAsync(report, basePercent, onProgress, ct)
                        .ConfigureAwait(false))
                {
                    var still = FindProcessorGeneratedMissing(report.McFolder, report.McVersion);
                    if (still.Count == 0)
                    {
                        onProgress?.Invoke(100, "补全完成", string.Empty);
                        return "缺失的资源已补全（加载器补丁文件已重新生成），现在可以重新启动游戏了";
                    }
                }

                onProgress?.Invoke(100, "需要重新安装加载器", string.Empty);
                return $"原版资源已补全，但 {names} 需要重新安装加载器才能生成"
                       + "（该文件由安装处理器本地打补丁产出，网上下载不到）";
            }

            onProgress?.Invoke(basePercent, "补全加载器依赖", string.Empty);
            await RepairLoaderLibrariesAsync(report, basePercent, onProgress, ct).ConfigureAwait(false);
        }

        onProgress?.Invoke(100, "补全完成", string.Empty);
        return "缺失的资源已补全，现在可以重新启动游戏了";
    }

    /// <summary>
    /// 找到版本清单里对应的条目。拿不到（断网 / 缓存里没有）就构造一个只带镜像地址的条目 ——
    /// <see cref="VanillaDownloader"/> 的版本 JSON 本来就有「官方 ↔ 镜像」双源兜底，
    /// 这里给 null 它也能走镜像，不必因为查不到清单就整个失败。
    /// </summary>
    private static async Task<VersionManifestEntry> ResolveManifestEntryAsync(
        string versionId, CancellationToken ct)
    {
        try
        {
            var all = await MinecraftVersionCache.GetAsync().ConfigureAwait(false);
            var hit = all.FirstOrDefault(v =>
                string.Equals(v.McVersion, versionId, StringComparison.OrdinalIgnoreCase));

            if (hit != null)
                return hit;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "补全资源：版本清单不可用，改用镜像地址兜底");
        }

        ct.ThrowIfCancellationRequested();

        return new VersionManifestEntry
        {
            Id = versionId,
            Type = "release",
            Url = DownloadSources.VersionJsonMirror(versionId),
            ReleaseTime = DateTime.MinValue,
        };
    }

    /// <summary>
    /// 尝试通过「重跑加载器安装」重建处理器产物（Forge / NeoForge 的 patched client jar）。
    ///
    /// <para>
    /// 这一步是必要的：那个 jar 是 binarypatcher 拿原版 client.jar + binpatch 现场打出来的，
    /// 任何 maven 上都没有（官方与 BMCLAPI 都 404）。所以「补全资源」不能靠下载，
    /// 只能把安装流程再走一遍 —— 库文件大多已在本地，会按体积跳过，实际只重跑处理器。
    /// </para>
    ///
    /// <para>
    /// 实例 id 形如 <c>26.2_forge_65.1.5</c> / <c>26.3_neoforge_26.3.0.0-beta</c>；
    /// 解析不出来就返回 false（交给上层给提示，不抛异常）。
    /// </para>
    /// </summary>
    private static async Task<bool> TryRebuildPatchedLibrariesAsync(
        IntegrityReport report,
        int basePercent,
        Action<int, string, string>? onProgress,
        CancellationToken ct)
    {
        var (neoForge, mcVersion, loaderVersion) = ParseForgeInstanceId(report.McVersion);
        if (string.IsNullOrEmpty(mcVersion) || string.IsNullOrEmpty(loaderVersion))
            return false;

        try
        {
            var javaPath = await JavaHelper.FindFirstJavaPathAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(javaPath))
            {
                Serilog.Log.Warning("补全资源：没有可用的 Java，无法重建加载器补丁文件");
                return false;
            }

            await LoaderInstaller.InstallForgeLikeAsync(
                report.McFolder, mcVersion, loaderVersion, report.McVersion,
                neoForge, javaPath,
                (percent, step) => onProgress?.Invoke(
                    basePercent + (int)(Math.Clamp(percent, 0, 100) * (100 - basePercent) / 100d),
                    step,
                    string.Empty),
                ct).ConfigureAwait(false);

            Serilog.Log.Information("补全资源：已重跑加载器安装来重建 patched jar（{Version}）", report.McVersion);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 重装失败不阻断：原版资源已经补好，上面那句提示仍然成立
            Serilog.Log.Warning(ex, "补全资源：重建加载器补丁文件失败（{Version}）", report.McVersion);
            return false;
        }
    }

    /// <summary>
    /// 从实例 id 里解析加载器类型与两个版本号。
    /// 形如 <c>&lt;mc&gt;_forge_&lt;ver&gt;</c> / <c>&lt;mc&gt;_neoforge_&lt;ver&gt;</c>，
    /// mc 版本本身也可能带下划线，所以按下划线切段后找关键字，而不是简单取第 2 段。
    /// </summary>
    private static (bool NeoForge, string McVersion, string LoaderVersion) ParseForgeInstanceId(string instanceId)
    {
        var parts = instanceId.Split('_');
        for (var i = 1; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("neoforge", StringComparison.OrdinalIgnoreCase))
            {
                return (true,
                    string.Join('_', parts[..i]),
                    string.Join('_', parts[(i + 1)..]));
            }

            if (parts[i].Equals("forge", StringComparison.OrdinalIgnoreCase))
            {
                return (false,
                    string.Join('_', parts[..i]),
                    string.Join('_', parts[(i + 1)..]));
            }
        }

        return (false, string.Empty, string.Empty);
    }

    /// <summary>
    /// 找出「没有下载地址、只能由安装处理器生成」的缺失库。
    ///
    /// <para>
    /// 判据刻意收得很窄：<c>downloads.artifact</c> 存在、<c>path</c> 存在、
    /// 但 <c>url</c> 是空字符串。这正是 Forge / NeoForge 的
    /// <c>forge-&lt;mc&gt;-&lt;ver&gt;-client.jar</c> 的写法 —— 它由 binarypatcher
    /// 拿原版 client.jar + binpatch 现场打补丁生成，任何 maven 上都没有。
    /// </para>
    ///
    /// <para>
    /// url 非空的普通库不在此列（那些交给下载器即可），缺失也不该拦住补全流程。
    /// </para>
    /// </summary>
    private static List<string> FindProcessorGeneratedMissing(string mcFolder, string versionId)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var current = versionId;
        for (var guard = 0; guard < 16; guard++)
        {
            var json = TryReadVersionJson(mcFolder, current);
            if (json is { } root
                && root.TryGetProperty("libraries", out var libs)
                && libs.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var lib in libs.EnumerateArray())
                {
                    if (!lib.TryGetProperty("downloads", out var dl)
                        || !dl.TryGetProperty("artifact", out var artifact)
                        || !artifact.TryGetProperty("path", out var pathEl))
                        continue;

                    var relative = pathEl.GetString();
                    if (string.IsNullOrWhiteSpace(relative))
                        continue;

                    // 有 url 的走正常下载，不在这里判死
                    var url = artifact.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url))
                        continue;

                    var full = Path.Combine(mcFolder, "libraries",
                        relative!.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(full) && seen.Add(full))
                        result.Add(Path.GetFileName(full));
                }
            }

            var parent = ReadInheritsFrom(mcFolder, current);
            if (string.IsNullOrWhiteSpace(parent))
                break;

            current = parent;
        }

        return result;
    }

    /// <summary>读版本 JSON（只取根元素，失败返回 null）。</summary>
    private static System.Text.Json.JsonElement? TryReadVersionJson(string mcFolder, string versionId)
    {
        try
        {
            var path = Path.Combine(mcFolder, "versions", versionId, versionId + ".json");
            if (!File.Exists(path))
                return null;

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadInheritsFrom(string mcFolder, string versionId)
    {
        var json = TryReadVersionJson(mcFolder, versionId);
        if (json is not { } root)
            return null;

        return root.TryGetProperty("inheritsFrom", out var inh) ? inh.GetString() : null;
    }

    /// <summary>补加载器自己的库（Forge / NeoForge / Fabric / Quilt 的 maven 制品）。</summary>
    private static async Task RepairLoaderLibrariesAsync(
        IntegrityReport report,
        int basePercent,
        Action<int, string, string>? onProgress,
        CancellationToken ct)
    {
        try
        {
            var items = CollectLoaderLibraryItems(report.McFolder, report.McVersion, out var generated);
            if (items.Count == 0)
            {
                if (generated > 0)
                {
                    Serilog.Log.Debug(
                        "补全加载器依赖：{Count} 条库声明是安装处理器产物，跳过（实例 {Version}）",
                        generated, report.McVersion);
                }

                onProgress?.Invoke(100, "补全完成", string.Empty);
                return;
            }

            var parallelism = SettingsService.ResolveDownloadConcurrency(8);

            var result = await BatchDownloader.DownloadAsync(
                items,
                parallelism,
                onProgress: p =>
                {
                    if (ct.IsCancellationRequested)
                        return;

                    var ratio = Math.Clamp(p.Percent / 100d, 0, 1);
                    onProgress?.Invoke(
                        basePercent + (int)(ratio * (100 - basePercent)),
                        p.TotalFiles > 0
                            ? $"补全加载器依赖 {p.CompletedFiles}/{p.TotalFiles}"
                            : "补全加载器依赖",
                        p.SpeedText);
                },
                ct: ct).ConfigureAwait(false);

            if (result.IsSuccess)
                return;

            var listed = string.Join(" / ", result.Failures.Take(3)
                .Select(f => $"{Path.GetFileName(f.File)}（{FabricDownloader.Describe(f.Error)}）"));

            Serilog.Log.Warning(
                "补全加载器依赖：{Count} 个库下载失败：{List}（实例 {Version}）",
                result.Failures.Count, listed, report.McVersion);

            onProgress?.Invoke(100, "原版资源已补全（部分加载器库未能补全）", string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 加载器库失败不阻断：原版资源已经补好了，用户至少能进原版。
            Serilog.Log.Warning(ex, "补全加载器依赖失败（不影响原版资源补全）：{Version}", report.McVersion);
            onProgress?.Invoke(100, "原版资源已补全（加载器依赖未能补全）", string.Empty);
        }
    }

    /// <summary>
    /// 从实例自己的版本 JSON 里收集加载器库的下载项。
    ///
    /// <para>
    /// <b>为什么不再用 <c>MinecraftResourceDownloader.VerifyAndDownloadDependenciesAsync</c></b>
    /// （2026-10-26 用户日志）：
    /// 它内部走 <c>MinecraftEntry.GetRequiredLibraries()</c> →
    /// <c>MinecraftLibrary.ParseJsonNode</c>，而 Fabric / Quilt 的 profile 里库声明是
    /// 「只有 <c>name</c> + <c>url</c>，没有 <c>downloads</c> / <c>sha1</c> / <c>size</c>」的形态，
    /// 会命中上游那句 <c>Sha1 = libNode.Sha1.Value</c> —— 对 null 的 <c>Nullable&lt;string&gt;</c>
    /// 取 <c>.Value</c>，抛 <c>InvalidOperationException: Nullable object must have a value</c>。
    /// 表现就是「补全加载器依赖失败」，而且整个加载器层一颗库都没补成。
    /// （同一个 bug 早在 Quilt 安装时就踩过，见 <see cref="LoaderInstaller.InstallQuiltAsync"/>。）
    /// </para>
    ///
    /// <para>
    /// 换成本项目自己的解析后还有两个附带好处：
    /// <list type="bullet">
    /// <item>只碰**加载器层**（实例 JSON 自己的 libraries）。原版的库由阶段 ① 按原版口径补，
    /// 这里不会再把 Minecraft 自带的库当成缺失重新下一遍 —— 那正是「有时候检查出来一堆 MC 内置库」的来源。</item>
    /// <item>落点路径与启动流程同一套规则（<c>downloads.artifact.path</c> → 退化按 maven 坐标拼），
    /// 已经存在的文件由下载内核按「体积 + zip 结构 + sha1」判跳过，不会重复下载。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="generated">被跳过的「安装处理器本地生成」条目数（maven 上没有，下不到）。</param>
    private static List<DownloadItem> CollectLoaderLibraryItems(
        string mcFolder, string instanceId, out int generated)
    {
        generated = 0;
        var items = new List<DownloadItem>();

        var root = TryReadVersionJson(mcFolder, instanceId);
        if (root is not { } json)
            return items;

        if (!json.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
            return items;

        var libsDir = Path.Combine(mcFolder, "libraries");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lib in libraries.EnumerateArray())
        {
            if (!InstanceIntegrityChecker.IsAllowedOnThisOs(lib))
                continue;

            if (!lib.TryGetProperty("name", out var nameElement)
                || nameElement.GetString() is not { Length: > 0 } name)
            {
                continue;
            }

            // downloads.artifact 在、但 url 是空串 = 安装处理器用 binarypatcher 本地生成的产物
            // （Forge / NeoForge 的 patched client jar 就是这种）。maven 上根本没有，
            // 进了下载队列必然 404，还会把「补全」整批判成失败。
            if (lib.TryGetProperty("downloads", out var downloads)
                && downloads.TryGetProperty("artifact", out var artifact))
            {
                var artifactUrl = artifact.TryGetProperty("url", out var urlElement)
                    ? urlElement.GetString()
                    : null;

                if (string.IsNullOrEmpty(artifactUrl))
                {
                    generated++;
                    continue;
                }
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(lib.GetRawText());
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "补全加载器依赖：库声明无法解析：{Name}", name);
                continue;
            }

            if (node is null)
                continue;

            var (url, relativePath, size, sha1) = LoaderInstaller.ResolveArtifact(node, name);
            if (url is null || relativePath is null)
            {
                Serilog.Log.Debug("补全加载器依赖：这条库声明拼不出地址，跳过：{Name}", name);
                continue;
            }

            var target = Path.Combine(libsDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!seen.Add(target))
                continue;

            items.Add(new DownloadItem(url, target, size, sha1, DisplayName: Path.GetFileName(target)));
        }

        return items;
    }
}
