using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinecraftLaunch.Base.Models.Network;
using MinecraftLaunch.Components.Installer;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// Minecraft 版本清单的进程内缓存（带磁盘缓存）。
    ///
    /// Mojang 的版本清单每次访问都要走一次 HTTPS，而且启动器里有多处（下载页、模组页的版本筛选、
    /// 安装器）都要用它。原先每个页面各自请求一次，导致点「下载中心」要等几秒才出列表。
    /// 这里合并成一次请求、全进程共享，并由 <see cref="Preload"/> 在主窗口显示后预热。
    ///
    /// 1.0.6.4 起补上错误捕捉：清单本身也是一次下载，弱网下会读到**被截断的响应体**
    /// （表现为 <c>JsonReaderException: Expected end of string, but instead reached end of data</c>），
    /// 以前一次抖动就让整页空掉，而且因为没有观察异常，日志里还会留一条
    /// 「未观察到的任务异常」——看着像崩溃，实际只是热身失败。
    ///
    /// 1.0.7.5 起再改两层（用户实测：关窗口时冒 <c>SocketException 995</c> / TaskCanceledException）：
    /// <list type="number">
    /// <item><b>磁盘缓存</b>：上次拉到过的清单存本地，下次启动直接秒出列表，不再让首屏等网络。</item>
    /// <item><b>双源并行竞速</b>：官方与 BMCLAPI **同时**发请求，谁先回完整的就用谁，
    /// 另一个立刻掐掉。原来是串行「一个源 15 秒 × 3 次，全败了才换另一个」，
    /// 最坏 90 秒都挂在后台 —— 用户随手关窗口就必然撞出「I/O 已中止」的中断异常。</item>
    /// </list>
    /// </summary>
    public static class MinecraftVersionCache
    {
        /// <summary>版本清单拉取的尝试次数（每个源各自计）。</summary>
        private const int MaxAttempts = 2;

        /// <summary>官方版本清单地址。</summary>
        private const string OfficialManifestUrl =
            "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

        /// <summary>
        /// BMCLAPI 的版本清单镜像。路径与官方完全一致，只是换了主机 ——
        /// 实测返回 200 且结构相同。
        /// </summary>
        private const string MirrorManifestUrl =
            "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

        /// <summary>
        /// 清单单次请求的超时。清单只有几百 KB，正常 1 秒内就到；
        /// 给 8 秒是为了让「卡住的主机」尽快让位给另一个源（两边本来就是并行跑的）。
        /// </summary>
        private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(8);

        /// <summary>
        /// 自己发请求的 HttpClient：关掉内置的 100 秒总超时，
        /// 每次请求改用独立 CTS 控制（内置超时罩不住「响应体读到一半」）。
        /// </summary>
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        private static readonly object SyncRoot = new();
        private static Task<List<VersionManifestEntry>>? _work;

        /// <summary>
        /// 磁盘缓存位置。放 <c>%LOCALAPPDATA%</c> 而不是程序目录 ——
        /// 发布版 exe 可能被放在 Program Files 这类不可写目录里，
        /// 写缓存失败不该影响任何功能（有缓存就用，没有就等网络）。
        /// </summary>
        private static string CacheFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibrantbitLauncher", "cache", "version_manifest_v2.json");

        /// <summary>获取版本清单（首次调用发起请求，之后复用同一结果）。</summary>
        public static Task<List<VersionManifestEntry>> GetAsync()
        {
            lock (SyncRoot)
            {
                // 失败的任务不留在缓存里：不清掉的话，一次网络抖动会让「下载页 / 模组页筛选」
                // 在整轮会话里反复拿到同一个已失败的 Task，怎么重试都还是同一个错误。
                if (_work is { IsFaulted: true })
                    _work = null;

                _work ??= LoadAsync();
            }

            return _work;
        }

        /// <summary>
        /// 拉取版本清单：**磁盘缓存优先 + 双源并行竞速**，不再依赖库的全局镜像开关。
        ///
        /// 旧的实现直接调 <c>VanillaInstaller.EnumerableMinecraftAsync()</c>，它只看
        /// <c>IsEnableMirror</c> 这一个全局开关：用户把下载源选成「官方源」时，
        /// 清单就只走 Mojang（国内经常连不上），于是**整个版本列表都加载不出来** ——
        /// 而这恰恰是最需要兜底的一次请求：拿不到清单，下载中心 / 安装页全是空的。
        /// </summary>
        private static async Task<List<VersionManifestEntry>> LoadAsync()
        {
            // ① 有磁盘缓存就先给一份 —— 版本清单一年也变不了几次，没必要让首屏等网络。
            //    读盘放到线程池：这是几百 KB 的 JSON，别压在 UI 线程上。
            var cached = await Task.Run(TryReadCache).ConfigureAwait(false);
            if (cached is { Count: > 0 })
            {
                // 后台再刷一次，成功后同时更新磁盘与进程内缓存（下次调用就能拿到新版本）。
                _ = RefreshInBackgroundAsync();
                return cached;
            }

            // ② 首次运行 / 缓存丢了，才必须等网络。
            return await LoadFromNetworkAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 双源并行竞速：官方与镜像同时发，先给出完整清单的胜出，另一个立刻取消。
        ///
        /// 为什么要并行而不是「先官方、失败再镜像」：用户设置的是「官方源」时，
        /// 串行意味着先白等最多 2×8 秒才轮到镜像；而这台机器上官方源大概率是不通的。
        /// 并行只多一个 HTTP 请求（几百 KB），换来的是「最快的那个源决定耗时」。
        /// </summary>
        private static async Task<List<VersionManifestEntry>> LoadFromNetworkAsync()
        {
            var urls = BuildManifestCandidates();
            Exception? last = null;

            using var raceCts = new CancellationTokenSource();
            var pending = urls.Select(u => FetchWithRetryAsync(u, raceCts.Token)).ToList();

            try
            {
                while (pending.Count > 0)
                {
                    var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(finished);

                    try
                    {
                        var entries = await finished.ConfigureAwait(false);
                        if (entries.Count > 0)
                            return entries;

                        last = new InvalidDataException("清单里没有任何版本记录");
                    }
                    catch (Exception ex)
                    {
                        // 单源失败不影响另一个源，记下来最后汇总成一句中文原因。
                        last = ex;
                    }
                }
            }
            finally
            {
                // 胜出后另一个源还在跑（或全部失败时可能还有尾巴），统一掐掉并观察其异常，
                // 否则会留一条「未观察到的任务异常」。
                raceCts.Cancel();
                foreach (var rest in pending)
                    _ = rest.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            }

            // 只有「网络能通、但结构解析不出来」才去问库（官方改结构时库升级能自愈）；
            // 纯网络失败就别再去白等库那 100 秒了 —— 那是用户关窗口时冒中断异常的主要来源之一。
            if (last is JsonException or InvalidDataException)
            {
                try
                {
                    var entries = await VanillaInstaller.EnumerableMinecraftAsync().ConfigureAwait(false);
                    if (entries.Any())
                        return entries.ToList();
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "库的版本清单接口也不可用（仅作结构变更兜底）");
                }
            }

            throw new DownloadFailedException(
                $"获取 Minecraft 版本清单失败（{Describe(last)}）。"
                + $"已尝试 {string.Join(" / ", urls.Select(DownloadSources.HostOf))}，"
                + $"每个源 {MaxAttempts} 次。请检查网络或代理后重试。",
                Array.Empty<string>(),
                last);
        }

        /// <summary>单个源：请求 + 解析，失败重试（次数很少，别把「卡住」叠乘成几十秒）。</summary>
        private static async Task<List<VersionManifestEntry>> FetchWithRetryAsync(
            string url, CancellationToken ct)
        {
            Exception? last = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var text = await FetchTextAsync(url, ct).ConfigureAwait(false);
                    var entries = ParseManifest(text);

                    if (entries.Count == 0)
                        throw new InvalidDataException("清单里没有任何版本记录");

                    // 成功即回报健康度：之前被标慢/坏的主机（比如上次超时）立刻恢复正常排序。
                    DownloadSources.ReportSourceHealth(url, SourceHealth.Good);
                    TryWriteCache(text);
                    return entries;
                }
                // ⚠️ 外层的取消（另一个源已胜出 / 应用正在退出）原样抛出，不参与重试。
                // 注意判据是「外部令牌是否已请求取消」而不是「异常是不是 OperationCanceledException」：
                // 单次请求超时用的是**内部** CTS，抛出来同样是 TaskCanceledException，
                // 那种要当普通失败处理并重试（2026-10-17 用户实测：`is not OperationCanceledException`
                // 的写法会把超时当用户取消放行，直接冒到界面弹英文）。
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Serilog.Log.Debug(ex,
                        "版本清单 {Host} 第 {Attempt}/{Max} 次失败",
                        DownloadSources.HostOf(url), attempt, MaxAttempts);

                    if (attempt < MaxAttempts)
                    {
                        try
                        {
                            await Task.Delay(300 * attempt, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                    }
                }
            }

            DownloadSources.ReportSourceHealth(url, SourceHealth.Failed);
            throw last ?? new InvalidDataException("未知错误");
        }

        /// <summary>请求清单文本（独立超时，不污染外层的竞速取消）。</summary>
        private static async Task<string> FetchTextAsync(string url, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ManifestTimeout);

            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }

        /// <summary>
        /// 清单候选地址：按设置决定先试官方还是先试镜像，被实测降级的主机排到后面。
        ///
        /// 注意这里是**并行**发出去的，顺序只影响「健康度相同时先看谁的结果」，
        /// 不再代表「谁先被请求」。
        /// </summary>
        private static List<string> BuildManifestCandidates()
        {
            var preferMirror = SettingsService.Current.UseBmclMirror;
            var list = new List<string>(2)
            {
                preferMirror ? MirrorManifestUrl : OfficialManifestUrl,
                preferMirror ? OfficialManifestUrl : MirrorManifestUrl,
            };

            return list
                .Where(u => !string.IsNullOrEmpty(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(u => DownloadSources.IsDemoted(u) ? 1 : 0)
                .ToList();
        }

        /// <summary>解析清单文本。</summary>
        private static List<VersionManifestEntry> ParseManifest(string text)
        {
            var list = new List<VersionManifestEntry>();

            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("versions", out var versions)
                || versions.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in versions.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "release";
                var downloadUrl = item.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;

                var releaseTime = DateTime.MinValue;
                if (item.TryGetProperty("releaseTime", out var timeEl)
                    && timeEl.GetString() is { Length: > 0 } rawTime)
                {
                    DateTime.TryParse(rawTime, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out releaseTime);
                }

                list.Add(new VersionManifestEntry
                {
                    Id = id,
                    Type = type ?? "release",
                    Url = downloadUrl,
                    ReleaseTime = releaseTime,
                });
            }

            return list;
        }

        /// <summary>读磁盘缓存；不存在或已损坏时返回 null（一律降级为「去网络拿」）。</summary>
        private static List<VersionManifestEntry>? TryReadCache()
        {
            try
            {
                var path = CacheFilePath;
                if (!File.Exists(path))
                    return null;

                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text))
                    return null;

                var entries = ParseManifest(text);
                if (entries.Count > 0)
                    Serilog.Log.Debug("版本清单：命中磁盘缓存 {Count} 条", entries.Count);

                return entries;
            }
            catch (Exception ex)
            {
                // 缓存坏了不是错误，删掉下次重写即可。
                Serilog.Log.Debug(ex, "读版本清单缓存失败（忽略，改走网络）");
                return null;
            }
        }

        /// <summary>写磁盘缓存；失败只记 Debug，不影响任何功能。</summary>
        private static void TryWriteCache(string text)
        {
            try
            {
                var path = CacheFilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, text);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "写版本清单缓存失败（忽略）");
            }
        }

        /// <summary>后台刷新：拿到新清单就替换缓存，失败静默（下次调用还会再试）。</summary>
        private static async Task RefreshInBackgroundAsync()
        {
            try
            {
                var fresh = await LoadFromNetworkAsync().ConfigureAwait(false);
                if (fresh.Count == 0)
                    return;

                lock (SyncRoot)
                {
                    _work = Task.FromResult(fresh);
                }
            }
            catch (Exception ex)
            {
                // 后台刷新失败完全不影响用户：当前会话用的还是磁盘缓存那份。
                Serilog.Log.Debug(ex, "版本清单后台刷新失败（继续使用缓存）");
            }
        }

        /// <summary>后台预热，失败不影响后续调用（下次会重新发起）。</summary>
        public static void Preload()
        {
            _ = GetAsync().ContinueWith(
                t =>
                {
                    if (!t.IsFaulted)
                        return;

                    // ⚠️ 这里**必须读一下 t.Exception**：只判 IsFaulted 不算「异常已观察」，
                    // 任务被 GC 时终结器会把它当「未观察到的任务异常」抛出来，
                    // 日志里就多一条带堆栈的 ERROR，看着像程序崩了（2026-10-10 冒烟测试实测到了）。
                    Serilog.Log.Warning(t.Exception, "版本清单预热失败（不影响使用，下次调用会自动重新请求）");
                    Reset();
                },
                TaskScheduler.Default);
        }

        private static void Reset()
        {
            lock (SyncRoot)
            {
                _work = null;
            }
        }

        /// <summary>把拉清单的异常翻成一句可读的原因。</summary>
        private static string Describe(Exception? ex)
        {
            if (ex is null)
                return "未知错误";

            if (ex is JsonException)
                return "服务器返回的数据不完整（响应被中途截断），通常是网络不稳";

            if (ex is OperationCanceledException or TimeoutException || ex.InnerException is TimeoutException)
                return "连接超时";

            if (ex is System.Net.Http.HttpRequestException)
                return "网络错误";

            return ex.Message;
        }
    }
}
