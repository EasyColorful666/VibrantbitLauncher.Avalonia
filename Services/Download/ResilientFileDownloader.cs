using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VibrantbitLauncher.Services.Download;

/// <summary>
/// 面对「每连接只给约 1 MB 就断」的网络时，仍然能把文件完整取回来的下载器。
///
/// <para>
/// <b>为什么需要它（而不是直接用 <see cref="FileDownloader"/>）</b>
/// </para>
///
/// <para>
/// 2026-10-18 实测（同一台机器、同一条线路）：
/// </para>
/// <list type="bullet">
/// <item><description>BMCLAPI 镜像取 Forge 安装器：连续 4 次 HTTP 200，但只有 89 KB / 171 KB / 284 KB / 2.2 MB（完整 6.0 MB），每次都被截断。</description></item>
/// <item><description>官方 maven.minecraftforge.net 取同一个文件：稳定停在 1,028,096 字节（正好 1 MB）不再增长。</description></item>
/// <item><description>用 Range 从 1 MB 处续取：能再拿到 633 KB，然后再次停住。</description></item>
/// </list>
///
/// <para>
/// 也就是说**单条连接的总配额大约就是 1 MB**，任何「一条连接下完整个文件」的实现都注定失败。
/// 但把同一份文件切成多段、每段一条新连接，就能一段段拼起来 —— 这正是这个类的做法：
/// 每轮从「已有字节数」处发 <c>Range: bytes=N-</c>，续写进同一个临时文件，
/// 直到凑够 Content-Range 里声明的总长。
/// </para>
///
/// <para>
/// 分块下载内核（<see cref="DownloadEngine"/>）本身也支持 Range，但它在失败时会清掉
/// 分块包（<c>ClearPackageOnCompletionWithFailure</c>），续传状态随之丢失，
/// 于是每次都从头再来、每次都卡在同一处。所以这一层不复用内核，自己管续传。
/// </para>
/// </summary>
internal static class ResilientFileDownloader
{
    /// <summary>
    /// 「首字节」上限：连接建立后这么久还没收到任何数据，就认定这个源不可用、换下一个。
    ///
    /// 必须比 <see cref="IdleTimeout"/> 短得多：GitHub Releases 在国内经常是
    /// 「TCP 能连、TLS 能握手、然后一个字节都不来」，如果按 25 秒等，光换个源就要白等半分钟。
    /// </summary>
    private static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>已经开始收数据之后的空闲上限：超过这个时间没有新字节就换连接续传。</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(25);

    /// <summary>最多尝试多少次「新连接续传」。6 MB / 0.6 MB ≈ 10 段，留足余量。</summary>
    private const int MaxAttempts = 30;

    /// <summary>连续多少次没有新增字节就放弃（避免对着一个死源空转到天荒地老）。</summary>
    private const int MaxConsecutiveNoProgress = 4;

    /// <summary>整个文件的总时限。慢可以，但不能无限。</summary>
    private static readonly TimeSpan TotalBudget = TimeSpan.FromMinutes(8);

    /// <summary>
    /// 下载 <paramref name="urls"/> 中任一地址到 <paramref name="targetPath"/>（带跨连接续传）。
    /// </summary>
    /// <param name="urls">候选地址，按顺序轮换使用（一个源卡住就换下一个，下一轮再换回来）。</param>
    /// <param name="targetPath">落点。下载过程中同目录会有 <c>.vblresume</c> 临时文件。</param>
    /// <param name="onProgress">进度回调（已下载字节，总字节；总字节未知时为 0）。可能从后台线程调用。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下载完成时为 true。</returns>
    public static async Task<bool> DownloadAsync(
        IReadOnlyList<string> urls,
        string targetPath,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        if (urls is null || urls.Count == 0)
            throw new ArgumentException("没有可用的下载地址", nameof(urls));

        var tempPath = targetPath + ".vblresume";
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 上次失败留下的残片是可以直接续的 —— 这正是本类存在的意义
        long have = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        long total = 0;
        var noProgressStreak = 0;
        var startedAt = Environment.TickCount64;

        using var http = CreateClient();

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (Environment.TickCount64 - startedAt > (long)TotalBudget.TotalMilliseconds)
            {
                Log.Warning("下载超时（{Budget} 分钟），已获取 {Have}/{Total} 字节",
                    TotalBudget.TotalMinutes, have, total);
                break;
            }

            var url = urls[attempt % urls.Count];

            // 已经确认下完了就不用再发请求
            if (total > 0 && have >= total)
                break;

            var before = have;

            try
            {
                have = await FetchRangeAsync(http, url, tempPath, have, onProgress,
                    v => total = v, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "续传失败（第 {Attempt} 次，{Url}），已获取 {Have} 字节",
                    attempt + 1, url, have);
            }

            if (have > before)
            {
                noProgressStreak = 0;
                Log.Debug("续传进度：{Have}/{Total} 字节（{Url}）", have, total, url);
            }
            else if (++noProgressStreak >= MaxConsecutiveNoProgress)
            {
                Log.Error("连续 {Count} 次没有任何进展，放弃下载：{File}", noProgressStreak, targetPath);
                break;
            }
        }

        // 没拿到总长时（服务端不给 Content-Range），只能靠调用方校验；
        // 拿到总长就必须严格相等，否则视为失败。
        var complete = have > 0 && (total == 0 || have == total);
        if (!complete)
        {
            Log.Error("下载不完整：{Have}/{Total} 字节 → {File}", have, total, targetPath);
            return false;
        }

        File.Move(tempPath, targetPath, overwrite: true);
        return true;
    }

    /// <summary>
    /// 发一次带 Range 的请求，把收到的字节**追加**到 <paramref name="tempPath"/>，
    /// 返回追加后的文件长度。中途断开不抛异常（返回已写入的长度），由调用方决定是否继续。
    /// </summary>
    private static async Task<long> FetchRangeAsync(
        HttpClient http,
        string url,
        string tempPath,
        long offset,
        Action<long, long>? onProgress,
        Action<long> setTotal,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0)
            request.Headers.Range = new RangeHeaderValue(offset, null);

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 先给「首字节」一个短上限；真正开始收数据后（见下面的 read 循环）再放宽到 idle
        idleCts.CancelAfter(FirstByteTimeout);

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idleCts.Token)
            .ConfigureAwait(false);

        // 416：offset 已经到底了，说明文件其实已完整
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return offset;

        response.EnsureSuccessStatusCode();

        // 服务端忽略 Range（返回 200 + 全量）：必须从头写，不能追加
        var restart = offset > 0 && response.StatusCode == HttpStatusCode.OK;
        if (restart)
        {
            Log.Warning("服务端不支持 Range，从头重下：{Url}", url);
            offset = 0;
        }

        // 总长：优先 Content-Range 的 "/total"，其次 Content-Length + offset
        long declared = 0;
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.Length is { } len && len > 0)
        {
            declared = len;
        }
        else if (response.Content.Headers.ContentLength is { } cl && cl > 0)
        {
            declared = restart ? cl : offset + cl;
        }

        if (declared > 0)
            setTotal(declared);

        await using var source = await response.Content.ReadAsStreamAsync(idleCts.Token).ConfigureAwait(false);
        await using var file = new FileStream(
            tempPath,
            restart ? FileMode.Create : FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        var written = restart ? 0L : offset;
        var buffer = new byte[81920];

        try
        {
            var gotAny = false;

            while (true)
            {
                // 收到第一块数据之后，才把超时放宽到 idle：
                // 首字节仍然用短超时兜着，避免「连上了但不发数据」的主机把整轮拖满。
                if (gotAny)
                    idleCts.CancelAfter(IdleTimeout);

                var read = await source.ReadAsync(buffer, idleCts.Token).ConfigureAwait(false);
                if (read <= 0)
                    break;

                gotAny = true;
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;

                DownloadSpeedometer.Report(read);
                onProgress?.Invoke(written, declared);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 空闲超时：这条连接被限速掐住了。已写入的字节保留，交给下一轮续传。
            Log.Debug("连接空闲超时，已写入 {Written} 字节，准备续传", written);
        }
        catch (IOException ex)
        {
            // 连接被对端提前关闭 —— 同样是「保留进度、下一轮续传」
            Log.Debug(ex, "连接提前关闭，已写入 {Written} 字节，准备续传", written);
        }

        return written;
    }

    /// <summary>
    /// 专用 HttpClient：不自动解压（Range 偏移必须对得上原始字节），
    /// 代理与环境变量同 <see cref="DownloadEngine"/> 保持一致。
    /// </summary>
    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            UseProxy = true,
            Proxy = WebRequest.DefaultWebProxy,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        var client = new HttpClient(handler)
        {
            // 单次请求的上限给足：真正的「卡住」由上面的空闲计时判定
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("VibrantbitLauncher/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }
}
