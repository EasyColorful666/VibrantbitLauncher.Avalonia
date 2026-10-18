using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MinecraftLaunch.Base.Models.Game;
using MinecraftLaunch.Base.Models.Network;
using MinecraftLaunch.Components.Downloader;
using MinecraftLaunch.Components.Parser;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 自实现的原版安装器。
///
/// 不用 MinecraftLaunch 的 <c>VanillaInstaller</c>，卡点是它的流程写死成
/// 「下载 versions/&lt;id&gt;/&lt;id&gt;.json → 立刻 MinecraftParser.Parse()」，
/// 而 1.13 以前的版本（含 1.12.2）的版本 JSON 没有 arguments 字段，
/// Parse 会当场抛 InvalidOperationException，详见 <see cref="VersionJsonNormalizer"/>。
/// 库没有留出「JSON 落盘之后、解析之前」的钩子，所以这里按同样的步骤自己走一遍：
///
/// 下载版本 JSON → 规范化（补 arguments / 修时区）→ 落盘
/// → 交给库的 MinecraftParser 解析（第 1 步做完就不会再崩）
/// → 交给库的 MinecraftResourceDownloader 下载 client jar / 库 / 资源索引与资源对象。
///
/// 重活仍然全部在库里，这里只负责「下载 + 修补 + 解析」这三步。
///
/// 1.0.6.4 起：每一步的异常都会被翻成中文可操作的 <see cref="DownloadFailedException"/>，
/// 不再把 <c>HttpRequestException</c> / <c>IOException</c> 的英文原文直接甩给用户；
/// 版本 JSON 也补上了「官方 ↔ BMCLAPI 镜像」双源兜底与重试。
/// </summary>
public static class VanillaDownloader
{
    /// <summary>
    /// 版本 JSON 单次请求的超时。故意不设长 —— 卡住要快速换源（另一个源或另一轮）。
    /// 同时把 HttpClient 自带的总超时关掉：它默认 100 秒，且罩不住响应体读到一半的情况。
    /// </summary>
    private static readonly TimeSpan MetaTimeout = TimeSpan.FromSeconds(15);

    private static readonly HttpClient _http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// 安装（或补全）一个原版版本。
    /// </summary>
    /// <param name="mcFolder">.minecraft 根目录。</param>
    /// <param name="entry">版本清单条目。</param>
    /// <param name="onProgress">进度回调 (百分比, 步骤文案, 速度文案)。</param>
    /// <param name="ct">
    /// 取消令牌。**必须一路传到库的下载器**：原版这一步动辄几百 MB，
    /// 以前没接令牌，用户在进度页点「取消安装」后仍然会一直下载，看起来就是「取消不了」。
    /// </param>
    public static async Task<MinecraftEntry> InstallAsync(
        string mcFolder,
        VersionManifestEntry entry,
        Action<int, string, string> onProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mcFolder))
            throw new ArgumentException("未配置 .minecraft 目录", nameof(mcFolder));
        if (entry == null || string.IsNullOrWhiteSpace(entry.Id))
            throw new ArgumentException("版本信息为空", nameof(entry));

        ct.ThrowIfCancellationRequested();

        onProgress?.Invoke(3, "下载版本信息", string.Empty);

        // 双源兜底：官方清单地址 + BMCLAPI 的 /version/<id>/json。谁先由设置决定，失败自动换另一个。
        var json = await DownloadJsonAsync(entry, ct);

        // 1.13 以前没有 arguments 字段，库解析时会崩；这一步是这次修复的核心
        var (normalized, _) = VersionJsonNormalizer.Normalize(json);
        json = normalized;

        var versionDir = Path.Combine(mcFolder, "versions", entry.Id);
        var jsonPath = Path.Combine(versionDir, entry.Id + ".json");
        try
        {
            Directory.CreateDirectory(versionDir);
            await File.WriteAllTextAsync(jsonPath, json, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DownloadFailedException(
                $"写入版本文件失败（{DownloadErrors.DescribeFileSystem(ex) ?? ex.Message}）：{jsonPath}",
                Array.Empty<string>(),
                ex);
        }

        ct.ThrowIfCancellationRequested();

        onProgress?.Invoke(8, "解析版本", string.Empty);
        var minecraft = new MinecraftParser(mcFolder).GetMinecraft(entry.Id)
            ?? throw new DownloadFailedException(
                $"版本文件已下载，但解析失败：{entry.Id}（版本信息可能不完整，请重试或换一个版本）",
                Array.Empty<string>(),
                null);

        onProgress?.Invoke(10, "准备下载依赖", string.Empty);
        var downloader = new MinecraftResourceDownloader(minecraft) { AllowVerifyAssets = true };
        downloader.ProgressChanged += (_, args) =>
        {
            var ratio = args.TotalCount > 0
                ? (double)args.CompletedCount / args.TotalCount
                : args.Percentage / 100d;

            // 原版这一阶段仍由 MinecraftLaunch 的下载器执行（它没有公开的注入点），
            // 所以速度取它自己报的值；但只要我们的新内核有活动（例如并行在装加载器库），
            // 就优先用全局速度表 —— 界面上的数字必须是「总速度」。
            var ours = DownloadSpeedometer.FormatSpeed();
            var speedText = string.IsNullOrEmpty(ours)
                ? (args.Speed > 0 ? DownloadSpeedometer.FormatSpeed(args.Speed) : string.Empty)
                : ours;

            onProgress?.Invoke(
                10 + (int)(Math.Clamp(ratio, 0, 1) * 88),
                $"下载文件 {args.CompletedCount}/{args.TotalCount}",
                speedText);
        };

        // 并发数以设置页的「下载线程数」为准（1~64 之间夹紧，避免误填把网络或磁盘打满）
        var parallelism = Math.Clamp(SettingsService.Current.DownloadThreads, 1, 64);

        try
        {
            // 库里这个重载本来就接受取消令牌 —— 以前没传，才导致点「取消安装」对原版下载毫无作用，
            // 几百 MB 照旧下完。这是「配装码安装取消不了」的主因。
            var result = await downloader.VerifyAndDownloadDependenciesAsync(parallelism, ct);

            var failedCount = result.Failed?.Count() ?? 0;
            if (failedCount > 0)
            {
                throw new DownloadFailedException(
                    $"有 {failedCount} 个依赖文件下载失败（可能是网络波动、磁盘空间不足，"
                    + "或文件被正在运行的游戏占用）。请在「设置 → 下载」调整下载源 / 线程数后重试。",
                    Array.Empty<string>(),
                    null);
            }
        }
        catch (DownloadFailedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 库在这里抛的异常可能是网络、也可能是写盘（磁盘满 / 文件被游戏占用），
            // 统一翻一遍，别让英文原文冒到界面上。
            throw new DownloadFailedException(
                $"下载依赖文件失败（{DownloadErrors.DescribeFileSystem(ex) ?? DescribeNetwork(ex)}）",
                Array.Empty<string>(),
                ex);
        }

        onProgress?.Invoke(100, "安装完成", string.Empty);
        return minecraft;
    }

    /// <summary>
    /// 下载版本 JSON：官方与镜像互为兜底，各按设置重试若干轮，每次请求独立超时。
    /// </summary>
    private static async Task<string> DownloadJsonAsync(VersionManifestEntry entry, CancellationToken ct = default)
    {
        var official = entry.Url;
        var mirror = DownloadSources.VersionJsonMirror(entry.Id);

        var candidates = new List<string>(2);
        void Add(string url)
        {
            if (!string.IsNullOrEmpty(url) && !candidates.Contains(url, StringComparer.OrdinalIgnoreCase))
                candidates.Add(url);
        }

        if (SettingsService.Current.UseBmclMirror)
        {
            Add(mirror);
            Add(official);
        }
        else
        {
            Add(official);
            Add(mirror);
        }

        if (candidates.Count == 0)
            throw new DownloadFailedException("版本清单里没有可用的下载地址", Array.Empty<string>(), null);

        // 实测被降级的主机（慢 / 坏）排到末尾，与 FabricDownloader 共用同一份健康度
        candidates = candidates.OrderBy(u => DownloadSources.IsDemoted(u) ? 1 : 0).ToList();

        var rounds = Math.Clamp(SettingsService.Current.DownloadRetryCount, 1, 10);
        Exception? last = null;

        for (var round = 0; round < rounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            if (round > 0)
                await Task.Delay(Math.Min(1500, 300 * round), ct);

            foreach (var url in candidates)
            {
                ct.ThrowIfCancellationRequested();

                var attempt = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    // 单次超时 + 外部取消：任一触发都结束这次请求，但要能区分是谁触发的
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(MetaTimeout);

                    using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var status = resp.StatusCode;
                        throw new HttpRequestException(
                            $"HTTP {(int)status} {status}", inner: null, statusCode: status);
                    }

                    var text = await resp.Content.ReadAsStringAsync(cts.Token);
                    attempt.Stop();
                    DownloadSources.ReportSourceHealth(
                        url, attempt.Elapsed.TotalSeconds > 15 ? SourceHealth.Slow : SourceHealth.Good);
                    return text;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 用户在进度页点了取消 → 别再换源重试，直接往上抛
                    throw;
                }
                catch (Exception ex)
                {
                    attempt.Stop();
                    last = ex;
                    DownloadSources.ReportSourceHealth(
                        url, attempt.Elapsed.TotalSeconds > 15 ? SourceHealth.Slow : SourceHealth.Failed);
                    Serilog.Log.Warning(ex, "版本信息下载失败：{Url}", url);
                }
            }
        }

        throw new DownloadFailedException(
            $"版本信息下载失败（{DescribeNetwork(last)}）。已尝试 "
            + $"{string.Join(" / ", candidates.Select(DownloadSources.HostOf))} 共 {rounds} 轮。"
            + "可在「设置 → 下载」把下载源切换为 BMCLAPI 镜像，或检查网络与代理后重试。",
            candidates,
            last);
    }

    /// <summary>把网络异常翻成一句可读的原因（文件系统类的错误由 DownloadErrors 处理）。</summary>
    private static string DescribeNetwork(Exception? ex)
    {
        if (ex is null)
            return "未知错误";

        if (ex is OperationCanceledException or TimeoutException || ex.InnerException is TimeoutException)
            return "连接超时";

        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode.HasValue)
            {
                var code = (int)httpEx.StatusCode.Value;
                return code switch
                {
                    404 => "服务器返回 404（该版本在下载源上不存在）",
                    >= 500 => $"服务器返回 {code}（下载源端故障，通常稍后重试即可）",
                    _ => $"服务器返回 {code}",
                };
            }

            return "网络错误";
        }

        return ex.Message;
    }
}
