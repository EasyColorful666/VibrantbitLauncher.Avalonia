using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services.Download;

/// <summary>
/// 批量下载：多个文件一起下，共用一张速度表，任何一个失败都不牵连其他文件。
///
/// <para>
/// <b>为什么不做「一个大下载器下所有文件」</b>：每个文件的分块状态是独立的，
/// 合并进一个 service 之后取消、续传、失败隔离都会互相污染。
/// 这里用「外层并发 N 个文件 × 内层每文件 M 块」，总连接数 = N×M，
/// 由调用方通过 <paramref name="parallelism"/> 控制 ——
/// 镜像端看我们就是「若干个正常的并发连接」，不会因为一个整合包几百个文件而被打成限流。
/// </para>
/// </summary>
public static class BatchDownloader
{
    /// <summary>进度回调的最小间隔 —— 再密就是给 UI 线程添堵。</summary>
    private const int ReportIntervalMs = 200;

    /// <summary>
    /// 下载一批文件。
    /// </summary>
    /// <param name="items">待下载清单。已经完整的文件会被跳过并计入 <see cref="BatchResult.Skipped"/>。</param>
    /// <param name="parallelism">同时下载几个文件（1~64）。</param>
    /// <param name="onProgress">进度回调（整体百分比 / 累计字节 / 全局速度 / 当前文件）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>
    /// 结果汇总。<b>不会因为个别文件失败而抛异常</b> —— 批量场景下「下完能下的、把失败的列出来」
    /// 比「一个失败全部白干」有用得多。是否成功看 <see cref="BatchResult.IsSuccess"/>。
    /// </returns>
    public static async Task<BatchResult> DownloadAsync(
        IReadOnlyList<DownloadItem> items,
        int parallelism,
        Action<BatchProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        var result = new BatchResult();
        if (items is null || items.Count == 0)
        {
            onProgress?.Invoke(new BatchProgress { Percent = 100 });
            return result;
        }

        var total = items.Count;

        // 只有「每个文件都带 size」时才敢用字节数算百分比，
        // 否则分母是残缺的，进度条会永远到不了 100%。
        var totalKnown = items.All(i => i.ExpectedSize > 0);
        var declaredTotal = totalKnown ? items.Sum(i => i.ExpectedSize) : 0;

        // 真正本次下载到的字节（跳过的文件不计），用于回报「这次下了多少」
        long received = 0;

        // 已结算的字节：下完 / 跳过 / 失败都算结算。
        // 用它算百分比，失败的文件才不会让进度条永远停在 97%。
        long settled = 0;
        var settledFiles = 0;

        // 结果里的总体积（下载 + 跳过）
        long totalBytes = 0;

        var clock = Stopwatch.StartNew();
        var lastReportTick = 0L;
        string currentFile = string.Empty;

        void Report(bool force)
        {
            // 多个文件并发回调，节流判断要串行化
            lock (result)
            {
                var now = clock.ElapsedMilliseconds;
                if (!force && now - lastReportTick < ReportIntervalMs)
                    return;
                lastReportTick = now;
            }

            var settledNow = Interlocked.Read(ref settled);
            var filesNow = Volatile.Read(ref settledFiles);

            var percent = totalKnown
                ? (int)Math.Clamp(settledNow * 100d / declaredTotal, 0, 100)
                : (int)Math.Clamp(filesNow * 100d / total, 0, 100);

            onProgress?.Invoke(new BatchProgress
            {
                Percent = percent,
                ReceivedBytes = Interlocked.Read(ref received),
                TotalBytes = declaredTotal,
                BytesPerSecond = DownloadSpeedometer.CurrentBytesPerSecond,
                CompletedFiles = filesNow,
                TotalFiles = total,
                CurrentFile = Volatile.Read(ref currentFile),
            });
        }

        Report(true);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(parallelism, 1, 64),
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(items, options, async (item, token) =>
        {
            token.ThrowIfCancellationRequested();

            // 只用来显示「当前在下一个什么」；并行时后写的会覆盖前面的，无所谓
            Volatile.Write(ref currentFile, item.Name);

            try
            {
                var downloaded = await FileDownloader.DownloadAsync(
                    item,
                    // 只统计这次真正下载到的字节
                    onBytes: delta => Interlocked.Add(ref received, delta),
                    onStatus: null,
                    ct: token).ConfigureAwait(false);

                if (downloaded)
                    result.Downloaded++;
                else
                    result.Skipped++;

                Interlocked.Add(ref totalBytes, item.ExpectedSize);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 用户取消要往外抛，让调用方收到「取消」而不是「失败」
                throw;
            }
            catch (Exception ex)
            {
                lock (result)
                    result.Failures.Add((item.TargetPath, ex));

                Serilog.Log.Warning(ex, "批量下载失败：{File}", item.TargetPath);
            }
            finally
            {
                // 失败也要结算，否则进度条卡住
                if (item.ExpectedSize > 0 && totalKnown)
                    Interlocked.Add(ref settled, item.ExpectedSize);

                Interlocked.Increment(ref settledFiles);
                Report(false);
            }
        }).ConfigureAwait(false);

        Report(true);
        result.TotalBytes = Interlocked.Read(ref totalBytes);
        return result;
    }
}
