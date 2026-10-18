using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Downloader;
using Serilog;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services.Download;

/// <summary>
/// 单文件下载：多连接（分块并行）+ 断点续传 + 校验 + 双源自动换源。
///
/// <para>
/// <b>落盘策略</b>：先下到同目录的 <c>&lt;name&gt;.&lt;guid&gt;.vblpart</c>，
/// 校验通过才 <see cref="File.Move(string, string, bool)"/> 到目标位置。
/// 目标路径要么不存在，要么是完整文件 —— 中途断网不会在 <c>mods/</c> 里留半截 .jar
/// 把游戏崩掉，也不会把已经能用的旧文件冲掉。
/// </para>
///
/// <para>
/// <b>为什么速度要往 <see cref="DownloadSpeedometer"/> 报</b>：这里只管「这个文件收到多少字节」，
/// 全局速度由速度表统一算。并行下 8 个文件时，界面上那个数字始终是「总下载速度」，
/// 不是某个文件的局部速度。
/// </para>
/// </summary>
public static class FileDownloader
{
    /// <summary>单次尝试超过这个时长就记一次「源偏慢」，后续文件会绕开它。</summary>
    private static readonly TimeSpan SlowSourceThreshold = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 下载一个文件。
    /// </summary>
    /// <param name="item">待下载项（URL / 目标路径 / 期望大小 / sha1 / 备用源）。</param>
    /// <param name="onBytes">收到的增量字节数，用于调用方自己算「本批次进度」。</param>
    /// <param name="onStatus">换源提示，例如「bmclapi2 失败，改用 piston-data」。</param>
    /// <param name="ct">取消令牌。**一路传到内核**，不能只在上层 return。</param>
    /// <param name="onProgress">
    /// 本次尝试的累计进度（已接收字节 / 总字节）。总字节为 0 表示服务器没报长度。
    /// 单文件场景（模组下载）直接拿它算百分比就行，不用自己攒增量。
    /// </param>
    /// <returns>真正下载返回 <c>true</c>；目标文件已完整存在、跳过时返回 <c>false</c>。</returns>
    /// <exception cref="DownloadFailedException">所有候选源都失败时。</exception>
    public static async Task<bool> DownloadAsync(
        DownloadItem item,
        Action<long>? onBytes = null,
        Action<string>? onStatus = null,
        CancellationToken ct = default,
        Action<long, long>? onProgress = null)
    {
        if (item is null)
            throw new ArgumentNullException(nameof(item));
        if (string.IsNullOrWhiteSpace(item.TargetPath))
            throw new ArgumentException("下载目标路径为空", nameof(item));

        if (IsUpToDate(item))
            return false;

        var dir = Path.GetDirectoryName(item.TargetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var candidates = BuildCandidates(item);
        if (candidates.Count == 0)
            throw new DownloadFailedException(
                $"没有可用的下载地址：{item.Name}", Array.Empty<string>(), null);

        var baseRounds = Math.Clamp(SettingsService.Current.DownloadRetryCount, 1, 10);
        var rounds = baseRounds;
        Exception? last = null;

        for (var round = 0; round < rounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            if (round > 0)
                await Task.Delay(BackoffMs(last, round), ct).ConfigureAwait(false);

            // 磁盘满 / 没权限 / 路径过长 —— 换源和重试都不会变好，直接带着原因失败
            if (DownloadErrors.IsFatal(last))
                break;

            for (var i = 0; i < candidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var url = candidates[i];
                var watch = Stopwatch.StartNew();

                try
                {
                    await DownloadCoreAsync(url, item, onBytes, onProgress, ct).ConfigureAwait(false);
                    watch.Stop();

                    DownloadSources.ReportSourceHealth(
                        url,
                        watch.Elapsed > SlowSourceThreshold ? SourceHealth.Slow : SourceHealth.Good);

                    return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 用户点了取消 —— 不要换源重试，直接往上抛
                    throw;
                }
                catch (Exception ex)
                {
                    watch.Stop();
                    last = ex;

                    // 限流不是「源坏了」，多给几轮机会，别让一次瞬时 403 变成安装失败
                    if (IsRateLimited(ex))
                        rounds = Math.Max(rounds, baseRounds + 3);

                    DownloadSources.ReportSourceHealth(
                        url,
                        watch.Elapsed > SlowSourceThreshold ? SourceHealth.Slow : SourceHealth.Failed);

                    if (candidates.Count > 1 && !DownloadErrors.IsFileInUse(ex) && i < candidates.Count - 1)
                    {
                        onStatus?.Invoke(
                            $"{DownloadSources.HostOf(url)} 失败，改用 {DownloadSources.HostOf(candidates[i + 1])}");
                    }

                    Log.Warning(ex, "下载失败（第 {Round} 轮，第 {Idx} 个候选源）：{Url} → {File}",
                        round + 1, i + 1, url, item.TargetPath);
                }
            }
        }

        throw new DownloadFailedException(
            $"下载失败（{Describe(last)}）：{item.Name}。"
            + $"已尝试 {string.Join(" / ", candidates.Select(DownloadSources.HostOf))} 共 {rounds} 轮。"
            + "可在「设置 → 下载」切换下载源或增大重试次数后重试。",
            candidates,
            last);
    }

    /// <summary>
    /// 只做一次尝试：一个 URL，一个临时文件，一次校验。
    /// 失败原因一律保留原始异常类型（换源判断要用 <see cref="DownloadErrors"/> 分类）。
    /// </summary>
    private static async Task DownloadCoreAsync(
        string url,
        DownloadItem item,
        Action<long>? onBytes,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        var tmpPath = MakeTempPath(item.TargetPath);
        // 库固定用「目标名 + .download」作为自己的临时文件，失败时可能留残片
        var packagePath = tmpPath + DefaultExtension;

        Exception? failure = null;
        var cancelled = false;
        long baseline = -1;
        // 本次尝试真正下载到的字节（用于单文件百分比，不受「续传基准」影响）
        long attemptReceived = 0;
        long totalBytes = 0;

        // 进度事件是**多块并发**回调的，不是串行队列。
        // 不加锁直接改 baseline，两帧交错就会把同一段字节算两次 ——
        // 界面上表现为「速度虚高、进度条跑过头」。锁里只有一次减法，开销可以忽略。
        var gate = new object();

        try
        {
            var chunks = Math.Clamp(SettingsService.Current.DownloadThreads, 2, 8);
            using var service = new DownloadService(DownloadEngine.CreateConfiguration(chunks));

            service.DownloadProgressChanged += (_, e) =>
            {
                var received = e.ReceivedBytesSize;

                lock (gate)
                {
                    if (e.TotalBytesToReceive > 0)
                        totalBytes = e.TotalBytesToReceive;

                    // 启用自动续传时，第一帧可能已经带着上次的进度 —— 只当基准，不报给速度表，
                    // 否则界面上会闪出一个几十 MB/s 的假峰值。
                    if (baseline < 0)
                    {
                        baseline = received;
                        return;
                    }

                    var delta = received - baseline;
                    baseline = received;
                    if (delta <= 0)
                        return;

                    attemptReceived += delta;
                    DownloadSpeedometer.Report(delta);
                    onBytes?.Invoke(delta);
                    onProgress?.Invoke(attemptReceived, totalBytes);
                }
            };

            // ⚠️ 这个库「失败不抛异常」：结果只从 DownloadFileCompleted 出来
            service.DownloadFileCompleted += (_, e) =>
            {
                lock (gate)
                {
                    if (e.Cancelled)
                        cancelled = true;
                    else if (e.Error is not null)
                        failure = e.Error;
                }
            };

            await service.DownloadFileTaskAsync(new[] { url }, tmpPath, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            lock (gate)
            {
                if (failure is not null)
                    ExceptionDispatchInfo.Capture(failure).Throw();

                if (cancelled)
                    throw new HttpRequestException("下载被中断（连接提前关闭）");
            }

            await VerifyAsync(tmpPath, item, ct).ConfigureAwait(false);

            File.Move(tmpPath, item.TargetPath, overwrite: true);
        }
        finally
        {
            TryDelete(tmpPath);
            TryDelete(packagePath);
        }
    }

    /// <summary>
    /// 校验下载结果。三道关：
    /// 非空 → 体积对得上 → sha1 对得上。
    ///
    /// <para>
    /// 体积这一关最能救「镜像返回了被截断的文件」：
    /// 分块下载时只要有一块提前结束，总长度就会短一截，但 HTTP 是 200，
    /// 不校验就会把它当成下好了。
    /// </para>
    /// </summary>
    private static async Task VerifyAsync(string path, DownloadItem item, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            throw DownloadIntegrityException.Empty();

        if (item.ExpectedSize > 0 && info.Length != item.ExpectedSize)
            throw DownloadIntegrityException.SizeMismatch(item.ExpectedSize, info.Length);

        if (string.IsNullOrWhiteSpace(item.Sha1))
            return;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        var hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();

        if (!string.Equals(actual, item.Sha1.Trim(), StringComparison.OrdinalIgnoreCase))
            throw DownloadIntegrityException.HashMismatch();
    }

    /// <summary>
    /// 目标文件是不是「已经下好了」。
    ///
    /// <para>
    /// 判据刻意保守：只有体积确认对得上才算数。不知道体积时只看存在性 ——
    /// 对已经在磁盘上的文件做 sha1 全量扫描会把启动前的准备时间拖成几十秒，
    /// 得不偿失（新下载的文件在 <see cref="VerifyAsync"/> 里已经验过了）。
    /// </para>
    /// </summary>
    private static bool IsUpToDate(DownloadItem item)
    {
        try
        {
            var info = new FileInfo(item.TargetPath);
            if (!info.Exists)
                return false;

            // 空文件一定是上次中断留下的残骸
            if (info.Length == 0)
                return false;

            return item.ExpectedSize <= 0 || info.Length == item.ExpectedSize;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 候选地址：主地址 + 显式备用地址 + 镜像改写，按「用户偏好 → 源健康度」排。
    /// </summary>
    private static List<string> BuildCandidates(DownloadItem item)
    {
        var list = new List<string>(3);

        void Add(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) &&
                !list.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(url);
            }
        }

        foreach (var candidate in DownloadSources.BuildCandidates(item.Url))
            Add(candidate);

        Add(item.AltUrl);

        // 稳定的健康度排序：被降级的主机挪到末尾，但绝不删除
        return list.OrderBy(u => DownloadSources.IsDemoted(u) ? 1 : 0).ToList();
    }

    /// <summary>每次尝试用独立的临时名，避免同名文件并发下载时互相锁死。</summary>
    private static string MakeTempPath(string targetPath)
        => $"{targetPath}.{Guid.NewGuid():N}.vblpart";

    /// <summary>库自己追加的临时后缀（见配置里的 DownloadFileExtension 默认值）。</summary>
    private const string DefaultExtension = ".download";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理失败不外抛：这个后缀的文件不参与校验
        }
    }

    /// <summary>是不是被镜像端限流（403 / 429 / 5xx）—— 这类错误值得多给几轮。</summary>
    private static bool IsRateLimited(Exception? ex)
        => ex is HttpRequestException { StatusCode: { } status }
           && ((int)status == 403 || (int)status == 429 || (int)status >= 500);

    /// <summary>轮与轮之间的退避：镜像站有瞬时并发限流，连续猛打只会一直吃 403。</summary>
    private static int BackoffMs(Exception? lastError, int round)
    {
        var baseDelay = IsRateLimited(lastError) ? 1200 : 400;
        return Math.Min(5000, baseDelay * round);
    }

    /// <summary>把异常翻成一句中文原因（文件系统优先，其次网络）。</summary>
    internal static string Describe(Exception? ex)
    {
        if (ex is null)
            return "未知错误";

        // 「下到了但内容不对」——必须排在文件系统判断之前，
        // 否则会被当成 IOException 报成「磁盘读写失败」（见 DownloadIntegrityException 的注释）。
        if (ex is DownloadIntegrityException integrity)
            return integrity.Message;

        var fs = DownloadErrors.DescribeFileSystem(ex);
        if (fs is not null)
            return fs;

        if (ex is HttpRequestException { StatusCode: { } status })
        {
            var code = (int)status;
            return code switch
            {
                403 or 429 => $"下载源限流（HTTP {code}）",
                404 => "文件在该下载源上不存在（404）",
                >= 500 => $"下载源端故障（HTTP {code}）",
                _ => $"服务器返回 HTTP {code}",
            };
        }

        if (ex is OperationCanceledException or TimeoutException)
            return "连接超时";

        if (ex is HttpRequestException)
            return "网络错误";

        if (ex is IOException io && io.InnerException is not null)
            return Describe(io.InnerException);

        return ex.Message;
    }
}
