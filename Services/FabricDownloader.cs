using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MinecraftLaunch.Base.Models.Game;
using MinecraftLaunch.Components.Parser;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 自实现 Fabric 下载安装器，绕过 MinecraftLaunch 库的 FabricInstaller / MinecraftResourceDownloader 的 bug。
    ///
    /// 1.0.6.1 修的是**网络层**。三个问题叠加起来会稳定复现「HTTP 超时」，
    /// 实测日志里用户的报错是（<c>FabricDownloader.cs:line 64</c>）：
    /// <code>
    /// The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.
    ///   ---> System.IO.IOException: Unable to read data from the transport connection
    ///        at System.Net.Http.HttpConnection.ChunkedEncodingReadStream.CopyToAsyncCore(...)
    /// </code>
    /// 具体是：
    /// ① 静态 HttpClient 用的是默认 <b>100 秒总超时</b>。meta.fabricmc.net 的 profile JSON
    ///    走 chunked 传输，在部分线路上会读到一半就卡死 → 用户干等 100 秒，
    ///    最后只拿到一句英文异常；
    /// ② <b>没有镜像支持</b>：设置页的「BMCLAPI 镜像」开关对 Fabric 分支完全无效
    ///    （实测日志：用户切到镜像后依旧超时）——因为这里的 URL 是写死的官方域名；
    /// ③ <b>没有重试</b>，而且库文件是一个一个串行下的。
    ///
    /// 现在：每个请求都有独立短超时（元数据 12s / 库文件 2min），
    /// 官方源与镜像**互为兜底**（按设置决定先后，各自动重试若干轮），
    /// 库文件并行下载、按声明大小算真实进度，落盘走 .tmp + 改名，
    /// 失败时抛中文可操作的 <see cref="DownloadFailedException"/>。
    /// </summary>
    public class FabricDownloader
    {
        /// <summary>元数据（版本列表 / profile JSON）单个请求的超时。故意取短 —— 失败要快速换源。</summary>
        private static readonly TimeSpan MetaTimeout = TimeSpan.FromSeconds(12);

        /// <summary>
        /// 库文件（jar）**单次尝试**的超时。
        ///
        /// 原来给的是 90 秒，实测下来这是个坑：BMCLAPI 的 /maven/ 会 302 跳到
        /// 一堆第三方免费节点（open-bmcl-api.lunar.cc / bmclapi1.sakura.exp.dog /
        /// minio.749333.xyz …），这些节点质量参差 —— 同一批 8 个库里，有的 0.8 秒下完，
        /// 有的 29 秒，asm-util 干脆连接超时。90 秒 × 2 个候选源 × 重试轮数，
        /// 单个文件最坏能磨十几分钟，用户看到的就是「卡在第 5 步：下载库」。
        ///
        /// Fabric 的库都很小（几十 KB ~ 1.5 MB），30 秒收不到响应就该换一次节点 ——
        /// 每次重试都会重新拿 302，很可能落到另一个健康的节点上。
        /// 「慢但一直在传」的情况由 <see cref="FileIdleTimeout"/> 兜住，不受这 30 秒影响。
        /// </summary>
        private static readonly TimeSpan FileTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 库文件的**空闲超时**：只要还在持续收到字节，deadline 就往后顺延。
        /// 这样「慢而稳」不会被 30 秒的整包上限砍掉，只有「彻底不动」才会判失败。
        /// </summary>
        private static readonly TimeSpan FileIdleTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// 单个库文件的**总时限**（含所有候选源与重试轮）。
        /// 没有它的话重试可以无限堆叠，安装看起来就是「卡死」。
        /// 90 秒还下不下来一个 ≤1.5 MB 的库，继续试的性价比已经很低了。
        /// </summary>
        private static readonly TimeSpan FileTotalTimeout = TimeSpan.FromSeconds(90);

        /// <summary>
        /// 单次请求超过这个耗时就算「慢源」，把该主机的健康度降级。
        ///
        /// 为什么要单独判「慢」：官方 maven.fabricmc.net 在国内常见的情况是
        /// **能通但极慢**（实测：8 个库磨了 145 秒，平均 18 秒一个）。它不报错，
        /// 所以不会触发换源；只有把「慢」也当成一种失败记下来，后面的库才会自动绕开它。
        ///
        /// 15 秒这个值取得比较紧是有意的：判断错也没有代价 ——
        /// 两边都被判慢时，候选顺序会退回用户偏好（稳定排序），与改动前一致。
        /// </summary>
        private static readonly TimeSpan SlowSourceThreshold = TimeSpan.FromSeconds(15);

        /// <summary>超过这个耗时就在日志里记一笔，方便远程排障时看清是哪个库慢。</summary>
        private static readonly TimeSpan SlowFileLogThreshold = TimeSpan.FromSeconds(5);

        /// <summary>元数据最多重试的轮数。用户的「重试次数」是给文件下载用的，元数据不该照搬（否则最长可等好几分钟）。</summary>
        private const int MetaMaxRounds = 2;

        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                // 连接阶段单独限时：连不上就赶紧换源，不必等整体超时。
                // 8 秒是折中 —— 正常的站点 1 秒内就建好连接，
                // 而弱网下的 DNS + TLS 握手也可能真的要好幾秒。
                ConnectTimeout = TimeSpan.FromSeconds(8),
                // 慢链路上 gzip 能省一大截时间（Fabric 版本列表未压缩就有 ~390 KB）
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(3),
                MaxConnectionsPerServer = 12,
            };

            return new HttpClient(handler)
            {
                // 关掉 HttpClient 自带的 100 秒总超时，改由每次请求的 CTS 精确控制。
                // 留着它的话，老问题会以「Timeout of 100 seconds elapsing」的形式原样回来。
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            };
        }

        /// <summary>是不是「被限流」——这两类错误必须重试，而且要多等一会儿再试。</summary>
        private static bool IsRateLimited(Exception? ex)
            => ex is HttpRequestException
            {
                StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests,
            };

        /// <summary>
        /// 轮与轮之间的退避时长。
        ///
        /// 文件被占用要等得再久一点：占用方（杀毒扫描 / 正在退出的游戏进程）通常几百毫秒内就会松手，
        /// 立刻重试往往还是撞在同一把锁上。限流等更久，普通错误短退避即可。
        /// </summary>
        private static int BackoffMs(Exception? lastError, int round)
        {
            if (DownloadErrors.IsFileInUse(lastError))
                return Math.Min(3000, 800 * round);

            return IsRateLimited(lastError) ? Math.Min(6000, 1500 * round) : Math.Min(1500, 300 * round);
        }

        /// <summary>
        /// 生成一个**本次尝试独占**的临时文件名。
        ///
        /// 旧实现固定用 <c>targetPath + ".vbltmp"</c>，只要两条并发的下载落到同一个目标 ——
        /// Fabric 清单里同一条库重复出现、或用户开了两个启动器实例 —— 后到的那个就会
        /// 被 Windows 判成「文件正被另一进程使用」而失败（用户 2026-10-10 报的就是这条）。
        /// 目标路径 + 随机后缀之后，任何两次尝试都不再可能撞到同一个临时文件。
        /// </summary>
        private static string MakeTempPath(string targetPath)
            => $"{targetPath}.{Guid.NewGuid():N}.vbltmp";

        /// <summary>旧版本用的固定临时后缀，启动时顺手清掉历史残留。</summary>
        private const string LegacyTempSuffix = ".vbltmp";

        /// <summary>
        /// 发一个 GET 并把「非 2xx」翻译成带状态码的异常。
        ///
        /// 不用 <c>EnsureSuccessStatusCode()</c>：它抛出的 <c>HttpRequestException</c>
        /// 不填 <c>StatusCode</c>，于是 400 / 404 / 502 全被归成 "Unknown"，
        /// 用户看到的提示毫无信息量。
        /// </summary>
        private static async Task<HttpResponseMessage> SendAsync(string url, CancellationToken token)
        {
            var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if (!resp.IsSuccessStatusCode)
            {
                var status = resp.StatusCode;
                resp.Dispose();
                throw new HttpRequestException(
                    $"HTTP {(int)status} {status}", inner: null, statusCode: status);
            }

            return resp;
        }

        /// <summary>
        /// 获取指定 MC 版本可用的 Fabric Loader 版本列表（含镜像兜底与重试）。
        /// </summary>
        /// <param name="mcVersion">Minecraft 版本，如 1.20.1。</param>
        /// <param name="onStatus">状态回调，用于把「正在换源」这类信息透到界面上。</param>
        public static async Task<List<FabricLoaderInfo>> EnumerableFabricAsync(
            string mcVersion,
            Action<string> onStatus = null,
            CancellationToken ct = default)
        {
            var url = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}";
            var json = await GetStringAsync(url, MetaTimeout, MetaMaxRounds, "Fabric 版本列表", onStatus, ct);

            var result = new List<FabricLoaderInfo>();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                if (!elem.TryGetProperty("loader", out var loader) || loader.ValueKind != JsonValueKind.Object)
                    continue;

                var version = loader.TryGetProperty("version", out var v) ? v.GetString() : null;
                if (string.IsNullOrEmpty(version))
                    continue;

                result.Add(new FabricLoaderInfo
                {
                    Version = version,
                    Maven = loader.TryGetProperty("maven", out var m) ? m.GetString() : null,
                    // 只有最新的那个 loader 是 stable=true，其余为 false 或缺字段
                    Stable = loader.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True,
                });
            }

            return result;
        }

        /// <summary>
        /// 完整安装 Fabric：下载 profile JSON → 修复格式 → 保存 → 下载所有库。
        /// </summary>
        /// <param name="mcFolder">.minecraft 根目录。</param>
        /// <param name="mcVersion">Minecraft 版本（如 1.20.1）。</param>
        /// <param name="fabricVersion">Fabric Loader 版本（如 0.15.11）。</param>
        /// <param name="customId">版本实例 ID（如 1.20.1_fabric_0.15.11）。</param>
        /// <param name="onProgress">进度回调 (percent, stepText)。**可能在工作线程上被调用**，调用方须自行切回 UI 线程。</param>
        public static async Task<MinecraftEntry> InstallAsync(
            string mcFolder,
            string mcVersion,
            string fabricVersion,
            string customId,
            Action<int, string> onProgress = null,
            CancellationToken ct = default)
        {
            onProgress?.Invoke(2, "下载 Fabric 版本信息");

            // 1. 下载 Fabric profile JSON
            var profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{fabricVersion}/profile/json";
            var json = await GetStringAsync(
                profileUrl, MetaTimeout, MetaMaxRounds, "Fabric 版本信息",
                s => onProgress?.Invoke(4, s), ct);

            // 2. 修复 JSON：DateTime 时区格式 +0000 → +00:00，id 改成 customId
            json = Regex.Replace(json, @"([+-]\d{2})(\d{2})""", "$1:$2\"");
            json = Regex.Replace(json, @"""id""\s*:\s*""[^""]*""", $"\"id\": \"{customId}\"");

            // 3. 保存 version JSON。写盘这一步也会失败（目录只读、磁盘满、路径过长），
            //    单拎出来翻译一下，别让用户面对一句裸的英文 IOException。
            var versionDir = Path.Combine(mcFolder, "versions", customId);
            var versionJsonPath = Path.Combine(versionDir, customId + ".json");
            try
            {
                Directory.CreateDirectory(versionDir);
                await File.WriteAllTextAsync(versionJsonPath, json, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new DownloadFailedException(
                    $"写入 Fabric 版本文件失败（{Describe(ex)}）：{versionJsonPath}",
                    Array.Empty<string>(),
                    ex);
            }

            // 4. 从 JSON 提取所有库的下载地址和路径
            onProgress?.Invoke(8, "解析依赖清单");
            var libraries = ExtractLibraries(json);
            if (libraries.Count == 0)
                throw new InvalidOperationException("Fabric 版本信息里没有任何可下载的依赖库，安装信息可能已损坏，请重新选择加载器版本");

            // 5. 下载所有库到 .minecraft/libraries/
            var libsDir = Path.Combine(mcFolder, "libraries");
            try
            {
                Directory.CreateDirectory(libsDir);
            }
            catch (Exception ex)
            {
                throw new DownloadFailedException(
                    $"无法创建依赖库目录（{Describe(ex)}）：{libsDir}",
                    Array.Empty<string>(),
                    ex);
            }

            var total = libraries.Count;
            var declaredBytes = libraries.Sum(x => x.Size);
            var done = 0;
            var bytesRead = 0L;
            var lastReportTick = 0L;
            var reportLock = new object();

            // 最近一次「换源 / 重试」的说明。挂到步骤文案上，用户才能看出
            // 卡在下载时其实还在动（只是 node 慢），而不是界面死住。
            var lastIssue = string.Empty;

            // 已经存在的库文件不必下载，但要把它们的大小先记进进度基数，
            // 否则「补装」时进度会从 0 开始、显得像没干活。
            long preexisting;
            foreach (var lib in libraries)
            {
                if (File.Exists(Path.Combine(libsDir, lib.Path)))
                    bytesRead += lib.Size;
            }
            preexisting = bytesRead;

            void Report(bool force)
            {
                var now = Environment.TickCount64;
                lock (reportLock)
                {
                    if (!force && now - lastReportTick < 200)
                        return;
                    lastReportTick = now;
                }

                var currentDone = Volatile.Read(ref done);
                var currentBytes = Interlocked.Read(ref bytesRead);

                // 10% ~ 93% 留给库下载，剩下的给解析
                int percent;
                if (declaredBytes > 0)
                    percent = 10 + (int)Math.Clamp((double)currentBytes / declaredBytes * 83, 0, 83);
                else
                    percent = 10 + (int)Math.Clamp((double)currentDone / total * 83, 0, 83);

                onProgress?.Invoke(percent, string.IsNullOrEmpty(lastIssue)
                    ? $"下载库 {currentDone}/{total}"
                    : $"下载库 {currentDone}/{total} · {lastIssue}");
            }

            // 并发数刻意比「下载线程数」低：Fabric 的库只有 ~8 个，
            // 实测按 DownloadThreads（默认 10）并发打 BMCLAPI 会被瞬时限流（403）——
            // 4 路已经比原来的串行快好几倍，同时不至于把镜像惹毛。
            var parallelism = Math.Clamp(SettingsService.Current.DownloadThreads, 1, 4);
            var stopwatch = Stopwatch.StartNew();

            // 单个库失败不再让整批一起陪葬：把失败收集起来，剩下的照样下完，
            // 最后一次性报出「哪些文件失败、各自为什么」。
            //
            // 原先 Parallel.ForEachAsync 里的第一个异常会直接向上抛，用户只能看到一个文件名，
            // 而且异常外面还裹着一层 AggregateException，真实原因经常在这一层被吞掉 ——
            // 表现的正是用户报的那种「报错信息里只有个文件名，看不出到底怎么了」。
            var failures = new ConcurrentBag<(string File, Exception Error)>();

            await Parallel.ForEachAsync(
                libraries,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                async (lib, token) =>
                {
                    var targetPath = Path.Combine(libsDir, lib.Path);
                    var targetDir = Path.GetDirectoryName(targetPath);

                    try
                    {
                        if (!string.IsNullOrEmpty(targetDir))
                            Directory.CreateDirectory(targetDir);

                        if (!File.Exists(targetPath))
                        {
                            await DownloadFileAsync(
                                lib.Url,
                                targetPath,
                                FileTimeout,
                                token,
                                delta =>
                                {
                                    Interlocked.Add(ref bytesRead, delta);
                                    Report(false);
                                },
                                // 每次换源 / 重试都把原因透到步骤文案上，
                                // 免得「卡在下载库」看起来像死机。
                                onStatus: s =>
                                {
                                    lastIssue = $"{Path.GetFileName(lib.Path)} {s}";
                                    Report(true);
                                },
                                idleTimeout: FileIdleTimeout,
                                totalTimeout: FileTotalTimeout);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // 用户主动取消要照常向外传播，不能当成「这一个库失败」咽掉
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures.Add((lib.Path, ex));
                        Serilog.Log.Warning(ex, "依赖库下载失败：{File}", lib.Path);
                        Report(true);
                        return;
                    }

                    Interlocked.Increment(ref done);
                    lastIssue = string.Empty;
                    Report(true);
                });

            if (!failures.IsEmpty)
            {
                throw new DownloadFailedException(
                    BuildBatchFailureMessage(failures, total),
                    failures.Select(f => f.File).ToList(),
                    failures.First().Error);
            }

            stopwatch.Stop();

            // 6. 用 MinecraftParser 解析已安装的版本
            onProgress?.Invoke(95, "解析版本");
            var parser = new MinecraftParser(mcFolder);
            var entry = parser.GetMinecraft(customId);
            if (entry == null)
            {
                var all = parser.GetMinecrafts();
                entry = all.FirstOrDefault(v => v.Id == customId)
                    ?? all.FirstOrDefault(v => v.Id.Contains("fabric") && v.Id.Contains(mcVersion));
            }

            Serilog.Log.Information(
                "Fabric 安装完成：{Id}（库 {Count} 个，本次新下载约 {MB:F1} MB，耗时 {Sec:F1}s）",
                customId, total,
                Math.Max(0, Interlocked.Read(ref bytesRead) - preexisting) / 1024d / 1024d,
                stopwatch.Elapsed.TotalSeconds);

            onProgress?.Invoke(100, "安装完成");
            return entry ?? throw new InvalidOperationException($"Fabric 安装完成但解析版本失败: {customId}");
        }

        /// <summary>
        /// 下载文本内容，官方源与镜像互为兜底。
        /// </summary>
        internal static async Task<string> GetStringAsync(
            string officialUrl,
            TimeSpan timeout,
            int maxRounds,
            string what,
            Action<string> onStatus,
            CancellationToken ct)
        {
            var candidates = DownloadSources.BuildCandidates(officialUrl);
            var maxAttemptRounds = Math.Clamp(maxRounds, 1, 10);
            Exception? last = null;

            for (var round = 0; round < maxAttemptRounds; round++)
            {
                // 轮与轮之间退避一下。镜像站有瞬时并发限流，
                // 连续猛打只会一直拿到 403 / 429 —— 实测 BMCLAPI 会这样。
                if (round > 0)
                    await Task.Delay(BackoffMs(last, round), ct);

                foreach (var url in candidates)
                {
                    ct.ThrowIfCancellationRequested();

                    var attempt = Stopwatch.StartNew();
                    try
                    {
                        // 超时必须同时罩住「读到响应体结束」这一步：
                        // 老 bug 正是 chunked 响应体读到一半卡住，而 100 秒总超时又太长。
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(timeout);

                        using var resp = await SendAsync(url, cts.Token);
                        var text = await resp.Content.ReadAsStringAsync(cts.Token);
                        attempt.Stop();

                        DownloadSources.ReportSourceHealth(url, SourceHealth.Good);
                        return text;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        attempt.Stop();
                        last = ex;

                        // 限流不是「源坏了」，多给几轮 + 长退避，别拿它当致命错误
                        if (IsRateLimited(ex))
                            maxAttemptRounds = Math.Max(maxAttemptRounds, maxRounds + 4);

                        DownloadSources.ReportSourceHealth(
                            url,
                            attempt.Elapsed > SlowSourceThreshold ? SourceHealth.Slow : SourceHealth.Failed);

                        // 同一个来源重试意义不大，先把另一个源试掉，别让用户干等
                        if (candidates.Count > 1)
                        {
                            var next = url == candidates[0] ? candidates[1] : candidates[0];
                            onStatus?.Invoke($"{what}：{DownloadSources.HostOf(url)} 失败，改用 {DownloadSources.HostOf(next)}");
                        }

                        Serilog.Log.Warning(ex, "{What} 下载失败：{Url}", what, url);
                    }
                }
            }

            throw new DownloadFailedException(
                $"{what}下载失败（{Describe(last)}）。已尝试 {string.Join(" / ", candidates.Select(DownloadSources.HostOf))} 共 {maxAttemptRounds} 轮。" +
                "可在「设置 → 下载」把下载源切换为 BMCLAPI 镜像，或检查网络与代理后重试。",
                candidates,
                last);
        }

        /// <summary>
        /// 下载一个文件到 <paramref name="targetPath"/>。
        ///
        /// <para>
        /// 1.0.7.15 起这一层只剩「转发」：真正的下载交给
        /// <see cref="FileDownloader"/>（第三方内核 + 分块并行 + 续传 + 校验）。
        /// 老实现是「单连接读流 + 手工 CTS 计时」，慢速链路上下一个 8 MB 的 Forge 安装器要几十秒，
        /// 而且所有超时口径都要在这里自己维护。
        /// </para>
        ///
        /// <para>
        /// <paramref name="timeout"/> / <paramref name="idleTimeout"/> / <paramref name="totalTimeout"/>
        /// 三个参数保留只是为了不动调用方签名 —— 内核的等价机制是
        /// <c>BlockTimeout</c>（多久没收到字节算卡死，30 秒）+ 分块级自动重试 +
        /// 候选源轮换，比「单文件总时限」更精确：慢而稳的下载不会被误杀，真卡死的 30 秒内就让位。
        /// </para>
        /// </summary>
        internal static async Task DownloadFileAsync(
            string officialUrl,
            string targetPath,
            TimeSpan timeout,
            CancellationToken ct,
            Action<long> onBytes = null,
            Action<string> onStatus = null,
            TimeSpan? idleTimeout = null,
            TimeSpan? totalTimeout = null)
        {
            // 老版本用固定名 .vbltmp 做临时文件，升级后不会有人再清理它，顺手删掉
            TryDelete(targetPath + LegacyTempSuffix);

            var item = new DownloadItem(
                officialUrl,
                targetPath,
                DisplayName: Path.GetFileName(targetPath));

            await FileDownloader.DownloadAsync(item, onBytes, onStatus, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 把一批失败拼成一条可读的提示：最多列 5 个文件名与各自原因，其余折叠成一句。
        ///
        /// 用户的直觉是「到底哪几个文件没下下来」，光给一个文件名他没法判断是网络问题
        /// 还是某个特定文件的问题 —— 后者可以用「另存为」手动补，前者才需要去改下载设置。
        /// </summary>
        private static string BuildBatchFailureMessage(
            IReadOnlyCollection<(string File, Exception Error)> failures,
            int total)
        {
            const int maxListed = 5;

            var lines = failures
                .Take(maxListed)
                .Select(f => $"· {Path.GetFileName(f.File)}：{Describe(f.Error)}");

            var more = failures.Count > maxListed
                ? $"\n（另有 {failures.Count - maxListed} 个文件同样失败）"
                : string.Empty;

            return $"{failures.Count}/{total} 个依赖库下载失败：\n{string.Join("\n", lines)}{more}\n" +
                   "可在「设置 → 下载」切换下载源、增大重试次数，或关闭正在运行的游戏后重试。";
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 清理失败无关紧要：这个后缀的文件不会参与校验
            }
        }

        /// <summary>把网络 / 文件系统异常翻成一句能看懂的原因，用于拼装报错文案。</summary>
        internal static string Describe(Exception? ex)
        {
            if (ex == null)
                return "未知错误";

            // AggregateException 的真实原因裹在 Inner 里，先拆开再判
            if (ex is AggregateException agg && agg.InnerException != null)
                ex = agg.InnerException;

            // 本地文件系统错误（文件被占用 / 磁盘满 / 没权限 / 路径过长）优先于网络分支：
            // 它们是「写盘」这一步的失败，与网络无关，混进网络文案会误导用户去改下载源。
            var fileSystem = DownloadErrors.DescribeFileSystem(ex);
            if (fileSystem is not null)
                return fileSystem;

            if (ex is OperationCanceledException or TimeoutException || ex.InnerException is TimeoutException)
                return "连接超时";

            if (ex is HttpRequestException httpEx)
            {
                if (httpEx.StatusCode.HasValue)
                {
                    var code = (int)httpEx.StatusCode.Value;
                    return code switch
                    {
                        404 => "服务器返回 404（该版本在 Fabric 源上不存在）",
                        400 => "服务器返回 400（请求的 Fabric 版本标识不被接受）",
                        403 or 429 => $"服务器限流（{code}）—— 镜像站在并发过高时会这样，稍后重试即可",
                        >= 500 => $"服务器返回 {code}（Fabric 源端故障，通常稍后重试即可）",
                        _ => $"服务器返回 {code}",
                    };
                }

                return httpEx.HttpRequestError == HttpRequestError.Unknown
                    ? "网络错误"
                    : $"网络错误（{httpEx.HttpRequestError}）";
            }

            return ex.Message;
        }

        /// <summary>
        /// 从 version JSON 中提取所有库的下载 URL、相对路径和声明大小。
        /// </summary>
        private static List<LibraryItem> ExtractLibraries(string json)
        {
            var result = new List<LibraryItem>();

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("libraries", out var libsElem) ||
                libsElem.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var lib in libsElem.EnumerateArray())
            {
                string url = null;
                string path = null;
                long size = 0;

                // 优先用 downloads.artifact（vanilla 格式）
                if (lib.TryGetProperty("downloads", out var dl) &&
                    dl.TryGetProperty("artifact", out var art))
                {
                    if (art.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                        url = u.GetString();
                    if (art.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                        path = p.GetString();
                    if (art.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number)
                        size = sz.GetInt64();
                }

                // 回退：maven 格式（url + name 拼接）。Fabric 的 profile JSON 都走这一支。
                if (url == null &&
                    lib.TryGetProperty("url", out var baseUrl) &&
                    lib.TryGetProperty("name", out var name))
                {
                    var parts = name.GetString()?.Split(':');
                    if (parts is { Length: >= 3 })
                    {
                        var group = parts[0].Replace('.', '/');
                        var artifact = parts[1];
                        var ver = parts[2];
                        path = $"{group}/{artifact}/{ver}/{artifact}-{ver}.jar";

                        var b = baseUrl.GetString();
                        if (!string.IsNullOrEmpty(b))
                            url = (b.EndsWith("/") ? b : b + "/") + path;
                    }
                }

                // 声明大小在顶层（maven 格式），拿它做进度分母
                if (size <= 0 &&
                    lib.TryGetProperty("size", out var topSize) &&
                    topSize.ValueKind == JsonValueKind.Number)
                {
                    size = topSize.GetInt64();
                }

                if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(path))
                    result.Add(new LibraryItem(url, path, Math.Max(0, size)));
            }

            // 按落点路径去重：Fabric 清单偶尔会把同一个 artifact 列两遍（甚至用两个不同的 name
            // 指向同一个 maven 路径）。重复的条目会让同一个目标被两条并行任务同时写，
            // 这正是「文件被另一进程占用」的另一条来路。保留先出现的那条即可。
            return result
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        private readonly record struct LibraryItem(string Url, string Path, long Size);
    }

    public class FabricLoaderInfo
    {
        public string Version { get; set; }
        public string Maven { get; set; }
        public bool Stable { get; set; }
        public string DisplayVersion => Version;
    }

    /// <summary>
    /// 「官方源与镜像都试过还是失败」时抛出的异常。
    ///
    /// 特意带上试过哪些源、失败原因，并把解决方向写进消息里：
    /// 之前用户只能看到一句
    /// <c>The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.</c>
    /// —— 既不是中文，也没告诉他下一步能做什么。
    /// </summary>
    public class DownloadFailedException : Exception
    {
        /// <summary>已经尝试过的下载源。</summary>
        public IReadOnlyList<string> TriedUrls { get; }

        public DownloadFailedException(string message, IReadOnlyList<string> triedUrls, Exception? inner)
            : base(message, inner)
        {
            TriedUrls = triedUrls ?? Array.Empty<string>();
        }
    }
}
