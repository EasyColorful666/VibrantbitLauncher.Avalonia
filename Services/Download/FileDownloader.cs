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
        Action<long, long>? onProgress = null,
        TimeSpan? idleTimeout = null)
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
                    await DownloadCoreAsync(url, item, onBytes, onProgress, idleTimeout, ct).ConfigureAwait(false);
                    watch.Stop();

                    // 镜像站「慢」不等于它坏了。
                    //
                    // BMCLAPI 的 /maven/ 每次都会 302 到不同的志愿者节点，这一次抽到慢节点，
                    // 下一次可能就是快的。原先只要单文件耗时超过 15 秒就把整个镜像主机判成
                    // Slow 并降级 10 分钟 —— 后面的库全部退回官方源，而
                    // maven.fabricmc.net 在国内实测只有 ~40 KB/s，
                    // 于是「装个 Fabric 库文件要等好几分钟」。
                    // 所以：镜像主机只要下成功了就记 Good（清掉旧账），只有官方源才按耗时判慢。
                    DownloadSources.ReportSourceHealth(
                        url,
                        watch.Elapsed > SlowSourceThreshold && !DownloadSources.IsMirrorHost(url)
                            ? SourceHealth.Slow
                            : SourceHealth.Good);

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
        TimeSpan? idleTimeout,
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

        // ==================== 字节级看门狗 ====================
        //
        // 为什么内核自带的 BlockTimeout 不够：
        // maven.minecraftforge.net / maven.neoforged.net 属于「**TCP 连得上、响应头也能秒回、
        // 但响应体一个字节都不发**」的那一类主机。内核的 BlockTimeout 判的是「这一块有没有
        // 新数据」，而它在分块握手完成前不计时，于是表现就是「安装器下载」这一步无限期停住 ——
        // 日志停在「下载 Forge 安装器」再也不动，取消都不好取消（用户唯一的出路是杀进程）。
        //
        // 这里在**调用方**再挂一层与内核无关的看门狗：
        //   · 从发起请求起 20 秒内一个字节都没收到  → 判定源不可用，掐掉这次尝试；
        //   · 收到数据之后连续 25 秒没有新字节        → 同上（弱网/限流下的假死）。
        // 两条都以「有没有字节」为准，所以慢而稳的链路不会被误杀。
        // 抛 TimeoutException 是为了让上层按「源故障 → 换下一个候选源」处理，
        // 而不是被当成用户取消。
        var idle = idleTimeout ?? DefaultIdleTimeout;
        var firstByteGrace = TimeSpan.FromSeconds(Math.Min(20, Math.Max(5, idle.TotalSeconds * 0.8)));

        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdogTripped = false;
        long lastActivityTicks = Environment.TickCount64;
        long firstByteAtTicks = 0;

        // 给用户看的一句话：区分「一个字节都没来」和「传了一半断掉」
        string StallDescription() =>
            Interlocked.Read(ref firstByteAtTicks) == 0
                ? $"连接后 {firstByteGrace.TotalSeconds:0} 秒内没有收到任何数据"
                : $"已有 {idle.TotalSeconds:0} 秒没有再收到数据";

        var watchdog = Task.Run(async () =>
        {
            var startedAt = Environment.TickCount64;

            while (!watchdogCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, watchdogCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var now = Environment.TickCount64;
                var gotFirstByte = Interlocked.Read(ref firstByteAtTicks) != 0;

                var stalled = gotFirstByte
                    ? now - Interlocked.Read(ref lastActivityTicks) > (long)idle.TotalMilliseconds
                    : now - startedAt > (long)firstByteGrace.TotalMilliseconds;

                if (!stalled)
                    continue;

                watchdogTripped = true;
                try { watchdogCts.Cancel(); } catch { /* 已释放 */ }
                return;
            }
        });

        try
        {
            var chunks = ResolveChunkCount();
            Log.Debug("下载 {File}：{Chunks} 块并行（{Mode}）",
                item.Name, chunks, chunks > 1 ? "多连接分块" : "单连接");

            using var service = new DownloadService(DownloadEngine.CreateConfiguration(chunks));

            service.DownloadProgressChanged += (_, e) =>
            {
                var received = e.ReceivedBytesSize;

                // 看门狗：有字节在动就续命（这是唯一的活动信号）
                Interlocked.Exchange(ref lastActivityTicks, Environment.TickCount64);
                if (Interlocked.Read(ref firstByteAtTicks) == 0 && received > 0)
                    Interlocked.Exchange(ref firstByteAtTicks, Environment.TickCount64);

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

            try
            {
                await service.DownloadFileTaskAsync(new[] { url }, tmpPath, watchdogCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && watchdogTripped)
            {
                // 看门狗掐断的，不是用户按了取消 —— 换成超时类型，
                // 上层才会按「换个源重试」处理，而不是把安装报成「已取消」。
                throw new TimeoutException(
                    $"{DownloadSources.HostOf(url)} 无响应：" + StallDescription());
            }

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            // 看门狗先于用户取消触发：这是「源假死」，不是用户行为
            if (watchdogTripped && !ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"{DownloadSources.HostOf(url)} 无响应：" + StallDescription());
            }

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
            try { watchdogCts.Cancel(); } catch { /* 已释放 */ }
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

        // jar / zip / mrpack 做一次「中央目录」校验。
        //
        // 为什么清单里没给 size 也必须验：install_profile 里的工具库是纯 maven 写法
        // （只有 name + url），既没有 size 也没有 sha1；而镜像站恰恰会「HTTP 200 + 传一半就断」。
        // 这种残片长度对不上任何声明值，sha1 也没有，会一路被当成「下载成功」，
        // 直到跑 Forge 处理器时报 ClassNotFoundException —— 用户完全看不出是下载问题。
        // 打开 ZipArchive 只读文件末尾的中央目录，几毫秒，代价可以忽略。
        //
        // .mrpack 是整合包（本质就是 zip），同样必须验：它没有 size/sha1，
        // 一个被截断的包要到解包阶段才炸，错误信息会变成「整合包格式不正确」，指不到网络上去。
        if (item.TargetPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            || item.TargetPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || item.TargetPath.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var probe = System.IO.Compression.ZipFile.OpenRead(path);
                _ = probe.Entries.Count;
            }
            catch (InvalidDataException)
            {
                throw new DownloadIntegrityException(
                    "下载到的 jar/zip 不完整（文件被截断，或下载源返回了错误页面）");
            }
        }

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
    /// 判据分三层，逐层加严：
    /// 1. 体积：清单给了 size 就必须完全相等；
    /// 2. 结构：jar / zip / mrpack 必须能读出中央目录；
    /// 3. 内容：清单给了 sha1 就真算一遍。
    /// </para>
    ///
    /// <para>
    /// <b>为什么第 2、3 层不能省</b>（实测踩过，代价很大）：镜像/socket 中断时会产生
    /// 「长度与清单完全一致、尾部却全被 0 填满」的坏包。Forge 26.2 的
    /// <c>forge-26.2-65.1.5-universal.jar</c> 与 <c>eventbus-7.0.5.jar</c> 就是这样 ——
    /// 只比体积的话每次都判「已下载」直接跳过，于是重装一百次也修不好，
    /// 启动时只看到 <c>zip END header not found</c>，用户完全猜不到是库文件坏了。
    /// 读中央目录只要几毫秒；sha1 是顺序读，几十 MB 的 jar 在 SSD 上是百毫秒级，
    /// 比「下完 500MB 才发现有一个坏包」便宜得多。
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

            if (item.ExpectedSize > 0 && info.Length != item.ExpectedSize)
                return false;

            if (IsArchivePath(item.TargetPath) && !IsReadableArchive(info.FullName))
                return false;

            if (string.IsNullOrWhiteSpace(item.Sha1))
                return true;

            using var stream = File.OpenRead(info.FullName);
            var actual = Convert.ToHexString(SHA1.HashData(stream));
            return string.Equals(actual, item.Sha1.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 读不了（被占用 / 权限）就当没下好，交给下载流程给出明确错误
            return false;
        }
    }

    /// <summary>这些扩展名的文件本质是 zip，损坏时必须能被识别出来。</summary>
    private static bool IsArchivePath(string path) =>
        path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase);

    /// <summary>能不能读出 zip 中央目录。截断包 / 错误页面在这里就会失败。</summary>
    private static bool IsReadableArchive(string path)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            _ = archive.Entries.Count;
            return true;
        }
        catch (InvalidDataException)
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

    /// <summary>设置缺失 / 非法时的回退分块数。4 块在多数家用带宽上已经能跑满。</summary>
    private const int DefaultChunkCount = 4;

    /// <summary>分块数硬上限（与 <see cref="DownloadEngine"/> 的 MaxChunks 保持一致）。</summary>
    private const int MaxChunkCount = 16;

    /// <summary>
    /// 取本次下载使用的分块数。
    ///
    /// <para>
    /// 用户的意图优先：设置页选了 1 就是单连接（镜像限流时的逃生阀），
    /// 选了 16 就切 16 块。只有值为 0 / 负数（旧配置没这个字段）才回退默认值。
    /// </para>
    /// </summary>
    internal static int ResolveChunkCount()
    {
        var configured = SettingsService.Current.DownloadChunkCount;
        if (configured <= 0)
            configured = DefaultChunkCount;

        return Math.Clamp(configured, 1, MaxChunkCount);
    }

    /// <summary>未显式指定时的空闲超时：40 秒没有任何字节就算这次尝试假死。</summary>
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(40);

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
