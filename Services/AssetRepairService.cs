using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>补加载器自己的库（Forge / NeoForge / Fabric / Quilt 的 maven 制品）。</summary>
    private static async Task RepairLoaderLibrariesAsync(
        IntegrityReport report,
        int basePercent,
        Action<int, string, string>? onProgress,
        CancellationToken ct)
    {
        try
        {
            var instance = new MinecraftParser(report.McFolder).GetMinecraft(report.McVersion);
            if (instance == null)
                return;

            // AllowVerifyAssets = false：资源已经在上一步按原版口径校验过，
            // 这里只关心加载器自己的库，不必把几千个资源对象再扫一遍。
            var downloader = new MinecraftResourceDownloader(instance) { AllowVerifyAssets = false };
            downloader.ProgressChanged += (_, args) =>
            {
                if (ct.IsCancellationRequested)
                    return;

                var ratio = args.TotalCount > 0
                    ? (double)args.CompletedCount / args.TotalCount
                    : args.Percentage / 100d;

                var ours = DownloadSpeedometer.FormatSpeed();
                onProgress?.Invoke(
                    basePercent + (int)(Math.Clamp(ratio, 0, 1) * (100 - basePercent)),
                    $"补全加载器依赖 {args.CompletedCount}/{args.TotalCount}",
                    string.IsNullOrEmpty(ours)
                        ? (args.Speed > 0 ? DownloadSpeedometer.FormatSpeed(args.Speed) : string.Empty)
                        : ours);
            };

            var parallelism = Math.Clamp(SettingsService.Current.DownloadThreads, 1, 64);
            // 取消令牌一路传下去：补资源动辄几个 GB，没有令牌点「取消」等于没点（1.0.7.15 修）
            await downloader.VerifyAndDownloadDependenciesAsync(parallelism, ct).ConfigureAwait(false);
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
}
