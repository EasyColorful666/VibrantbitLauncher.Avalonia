using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Services;

/// <summary>一次测速的结果。</summary>
public sealed class SpeedTestResult
{
    /// <summary>测得的是哪个源（主机名）。</summary>
    public string Host { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    /// <summary>本次用的连接数。</summary>
    public int Threads { get; init; }

    /// <summary>单连接速度（字节/秒）。</summary>
    public double SingleBytesPerSecond { get; init; }

    /// <summary>多连接合计速度（字节/秒）。</summary>
    public double MultiBytesPerSecond { get; init; }

    /// <summary>多连接阶段的瞬时峰值（字节/秒）。</summary>
    public double PeakBytesPerSecond { get; init; }

    /// <summary>多连接阶段实际读到的字节数。</summary>
    public long MultiBytes { get; init; }

    /// <summary>多连接阶段耗时（秒）。</summary>
    public double MultiSeconds { get; init; }

    /// <summary>对端是否支持 Range（不支持时只能单连接，测不出多线程收益）。</summary>
    public bool RangeSupported { get; init; } = true;

    public bool Ok { get; init; } = true;

    /// <summary>失败原因 / 备注（中文原文）。</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>多连接相对单连接的提升倍数（0 表示单连接没测出来，无法比较）。</summary>
    public double Gain => SingleBytesPerSecond > 0
        ? MultiBytesPerSecond / SingleBytesPerSecond
        : 0;

    public string SingleText => SpeedTestService.FormatSpeed(SingleBytesPerSecond);

    public string MultiText => SpeedTestService.FormatSpeed(MultiBytesPerSecond);

    public string PeakText => SpeedTestService.FormatSpeed(PeakBytesPerSecond);

    public string GainText => Gain <= 0 ? "—" : $"{Gain:0.0}×";
}

/// <summary>
/// 多线程测速：先量一条连接，再同时开 N 条，把两个数字一起给用户看。
///
/// <para>
/// <b>为什么一定要单连接基准</b>：只报一个「合计 30 MB/s」没法判断该不该调并发 ——
/// 单连接就有 25 MB/s 说明链路已经跑满，再加连接只是给镜像添压力；
/// 单连接只有 1 MB/s、8 连接能到 8 MB/s，才说明对端在做单连接限速、多开确实有用。
/// </para>
///
/// <para>
/// 测试目标就是用户当前设置里真正的下载源（自定义源优先），
/// 所以测出来的速度对「要不要换源」是有直接参考价值的。
/// </para>
/// </summary>
public static class SpeedTestService
{
    /// <summary>单连接阶段的时限与字节上限（先到者结束）。</summary>
    private static readonly TimeSpan SingleDuration = TimeSpan.FromSeconds(4);
    private const long SingleByteCap = 8L * 1024 * 1024;

    /// <summary>多连接阶段：整个阶段共用的时限，以及每个连接的上限。</summary>
    private static readonly TimeSpan MultiDuration = TimeSpan.FromSeconds(6);
    private const long PerThreadByteCap = 16L * 1024 * 1024;

    /// <summary>拿不到版本清单时的兜底测试版本 —— 各家镜像都留着的老版本，命中率高。</summary>
    private const string FallbackVersion = "1.20.1";

    /// <summary>小于这个体积不谈速（响应头开销占比太高，数字会失真）。</summary>
    private const long MinTestableSize = 2L * 1024 * 1024;

    /// <summary>把字节/秒格式化成界面上那串速度。</summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
            return "0 KB/s";

        return bytesPerSecond >= 1024d * 1024d
            ? string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / 1024d / 1024d:0.0} MB/s")
            : string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / 1024d:0} KB/s");
    }

    /// <summary>
    /// 跑一次测速。<paramref name="status"/> 用来把「正在测单连接 / 正在测 N 连接」报给界面。
    /// </summary>
    public static async Task<SpeedTestResult> RunAsync(
        int threads,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        threads = Math.Clamp(threads, 1, 32);

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = true,
            Proxy = DownloadEngine.BuildProxy(),
            // 测速要的是「真实传输的字节数」，让服务器压缩会把数字算歪
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = threads + 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        try
        {
            var target = await ResolveTargetAsync(client, ct).ConfigureAwait(false);
            if (target is null)
            {
                return new SpeedTestResult
                {
                    Ok = false,
                    Detail = "拿不到可用的测试文件：版本清单和镜像都没回应，先做一次网络体检看看是哪一段断了",
                };
            }

            var (url, size, rangeSupported) = target.Value;

            status?.Report("正在测单连接…");
            var single = await MeasureAsync(
                client, url, threads: 1, SingleByteCap, SingleDuration, rangeSupported, ct)
                .ConfigureAwait(false);

            status?.Report($"正在测 {threads} 连接…");
            var multi = await MeasureAsync(
                client, url, threads, PerThreadByteCap, MultiDuration, rangeSupported, ct)
                .ConfigureAwait(false);

            var result = new SpeedTestResult
            {
                Host = DownloadSources.HostOf(url),
                Url = url,
                Threads = threads,
                SingleBytesPerSecond = single.Speed,
                MultiBytesPerSecond = multi.Speed,
                PeakBytesPerSecond = multi.Peak,
                MultiBytes = multi.Bytes,
                MultiSeconds = multi.Seconds,
                RangeSupported = rangeSupported,
                Ok = multi.Speed > 0,
                Detail = BuildAdvice(single.Speed, multi.Speed, rangeSupported),
            };

            // 测出来的好坏顺手喂给源健康度：慢到离谱的源，后面下载时会被自动排到后面
            DownloadSources.ReportSourceHealth(
                url,
                result.Ok ? SourceHealth.Good : SourceHealth.Slow);

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "测速失败");
            return new SpeedTestResult
            {
                Ok = false,
                Threads = threads,
                Detail = "测速失败：" + ex.Message,
            };
        }
    }

    private static string BuildAdvice(double single, double multi, bool rangeSupported)
    {
        if (multi <= 0)
            return "一个字节都没测到，先做一次网络体检";

        if (!rangeSupported)
            return "这个源不支持分块下载，只能单连接 —— 想提速请换一个支持 Range 的源";

        if (single > 0 && multi < single * 1.2 && multi > 8L * 1024 * 1024)
            return "多连接几乎没提升：链路本身已经跑满了，再把并发调高意义不大";

        if (multi < 1.5 * 1024 * 1024)
            return "速度偏低：换个下载源，或把单文件分块数调到 16 以上再测一次";

        if (single > 0 && multi >= single * 2.5)
            return "多连接收益明显：这个源在单连接上限速，可以适当调高单文件分块数";

        return "速度正常";
    }

    /// <summary>
    /// 选一个测试目标：优先当前生效的下载源，拿不到官方 client.jar 的地址就只用镜像。
    /// 返回 (地址, 体积, 是否支持 Range)。
    /// </summary>
    private static async Task<(string Url, long Size, bool RangeSupported)?> ResolveTargetAsync(
        HttpClient client, CancellationToken ct)
    {
        var (version, officialClientUrl) = await ResolveManifestAsync(client, ct).ConfigureAwait(false);

        var candidates = new List<string>();

        var preferOfficial = SettingsService.DownloadSourceMode == "Official";
        if (preferOfficial && !string.IsNullOrEmpty(officialClientUrl))
            candidates.Add(officialClientUrl);

        foreach (var root in DownloadSources.ActiveMirrorRoots)
            candidates.Add($"{root}/version/{version}/client");

        if (!string.IsNullOrEmpty(officialClientUrl))
            candidates.Add(officialClientUrl);

        foreach (var url in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ct.IsCancellationRequested)
                return null;

            var probe = await ProbeSizeAsync(client, url, ct).ConfigureAwait(false);
            if (probe is { } p && p.Size >= MinTestableSize)
                return (url, p.Size, p.RangeSupported);
        }

        return null;
    }

    /// <summary>
    /// 版本清单 → 最新正式版 + 它的 client.jar 地址。
    ///
    /// 任何一步失败都只是「拿不到官方地址」，不影响用镜像测速，
    /// 所以这里把所有异常都吞掉、退回到兜底版本。
    /// </summary>
    private static async Task<(string Version, string? OfficialClientUrl)> ResolveManifestAsync(
        HttpClient client, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
            request.Headers.UserAgent.ParseAdd("VibrantbitLauncher/1.0 (+speedtest)");

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (FallbackVersion, null);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, default, timeout.Token).ConfigureAwait(false);

            var root = document.RootElement;
            var version = root.TryGetProperty("latest", out var latest)
                          && latest.TryGetProperty("release", out var release)
                ? release.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(version))
                return (FallbackVersion, null);

            string? clientUrl = null;
            if (root.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in versions.EnumerateArray())
                {
                    if (!entry.TryGetProperty("id", out var id)
                        || !string.Equals(id.GetString(), version, StringComparison.OrdinalIgnoreCase)
                        || !entry.TryGetProperty("url", out var urlElement))
                    {
                        continue;
                    }

                    clientUrl = await ReadClientUrlAsync(client, urlElement.GetString(), timeout.Token)
                        .ConfigureAwait(false);
                    break;
                }
            }

            return (version!, clientUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Serilog.Log.Debug(ex, "测速：版本清单不可用，改用兜底版本 {Version}", FallbackVersion);
            return (FallbackVersion, null);
        }
    }

    private static async Task<string?> ReadClientUrlAsync(HttpClient client, string? versionJsonUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(versionJsonUrl))
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, versionJsonUrl);
            request.Headers.UserAgent.ParseAdd("VibrantbitLauncher/1.0 (+speedtest)");

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

            return document.RootElement.TryGetProperty("downloads", out var downloads)
                   && downloads.TryGetProperty("client", out var clientEntry)
                   && clientEntry.TryGetProperty("url", out var urlElement)
                ? urlElement.GetString()
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Serilog.Log.Debug(ex, "测速：版本 JSON 不可用");
            return null;
        }
    }

    /// <summary>用 <c>Range: bytes=0-0</c> 问一下体积，顺便确认对端支不支持分块。</summary>
    private static async Task<(long Size, bool RangeSupported)?> ProbeSizeAsync(
        HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            request.Headers.UserAgent.ParseAdd("VibrantbitLauncher/1.0 (+speedtest)");

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            var rangeSupported = response.StatusCode == HttpStatusCode.PartialContent;
            var size = response.Content.Headers.ContentRange?.Length
                       ?? response.Content.Headers.ContentLength
                       ?? 0;

            return (size, rangeSupported);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Serilog.Log.Debug(ex, "测速：{Url} 不可用", url);
            return null;
        }
    }

    /// <summary>
    /// 开 <paramref name="threads"/> 条连接各读一段，返回合计速度与峰值。
    ///
    /// <para>
    /// 不支持 Range 时只有第一条连接有效（其余会拿到整个文件的开头），
    /// 所以那种情况下直接退化成单连接，并把 <c>RangeSupported</c> 标出来。
    /// </para>
    /// </summary>
    private static async Task<(double Speed, double Peak, long Bytes, double Seconds)> MeasureAsync(
        HttpClient client,
        string url,
        int threads,
        long perThreadByteCap,
        TimeSpan duration,
        bool rangeSupported,
        CancellationToken ct)
    {
        var effectiveThreads = rangeSupported ? threads : 1;
        var slice = perThreadByteCap;

        long totalBytes = 0;
        var stopwatch = Stopwatch.StartNew();

        using var phase = CancellationTokenSource.CreateLinkedTokenSource(ct);
        phase.CancelAfter(duration);

        var peak = 0d;
        var samplerDone = false;
        var sampler = Task.Run(async () =>
        {
            var last = 0L;
            var lastAt = Stopwatch.StartNew();
            while (!samplerDone)
            {
                try
                {
                    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                var now = Interlocked.Read(ref totalBytes);
                var elapsed = lastAt.Elapsed.TotalSeconds;
                if (elapsed > 0.05)
                {
                    var instant = (now - last) / elapsed;
                    if (instant > peak)
                        peak = instant;
                }

                last = now;
                lastAt.Restart();
            }
        });

        var tasks = new List<Task>(effectiveThreads);
        for (var i = 0; i < effectiveThreads; i++)
        {
            var from = i * slice;
            var to = from + slice - 1;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await PumpAsync(client, url, from, to, rangeSupported, phase.Token,
                        add => Interlocked.Add(ref totalBytes, add)).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 到时限了，正常收尾
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "测速连接中断：{Url}", url);
                }
            }, CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            samplerDone = true;
            stopwatch.Stop();
            await sampler.ConfigureAwait(false);
        }

        var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        var bytes = Interlocked.Read(ref totalBytes);
        return (bytes / seconds, peak, bytes, seconds);
    }

    /// <summary>读一段 <c>[from, to]</c> 的字节，读到时限或读满为止。</summary>
    private static async Task PumpAsync(
        HttpClient client,
        string url,
        long from,
        long to,
        bool rangeSupported,
        CancellationToken ct,
        Action<int> report)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (rangeSupported)
            request.Headers.Range = new RangeHeaderValue(from, to);
        request.Headers.UserAgent.ParseAdd("VibrantbitLauncher/1.0 (+speedtest)");

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[96 * 1024];
        long read = 0;
        var limit = to - from + 1;
        while (read < limit)
        {
            var want = (int)Math.Min(buffer.Length, limit - read);
            var got = await stream.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (got <= 0)
                break;

            read += got;
            report(got);
        }
    }
}
