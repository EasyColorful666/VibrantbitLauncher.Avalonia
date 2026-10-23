using System;
using System.Net;
using System.Net.Http;
using Downloader;

namespace VibrantbitLauncher.Services.Download;

/// <summary>
/// 下载内核（基于 <c>Downloader</c> 5.10.1）的统一配置。
///
/// <para>
/// 换成第三方内核要解决的是老实现的三个硬伤：
/// ① <b>单连接</b> —— 每个文件一条 TCP 流，慢速链路上单个大文件（client.jar、加载器安装器）
///    完全靠运气；
/// ② <b>速度反馈是「局部」的</b> —— 原来每个下载点自己算 MB/s，并行下 8 个文件时
///    界面上显示的是「某个文件」的速度，和用户看到的进度条刻度对不上；
/// ③ <b>没有断点续传</b> —— 一次读流失败就从头再来。
/// </para>
///
/// <para>
/// 这里只放「怎么连」，不放「怎么下」：连接数/分块策略、超时口径、代理、UA 都在这一处，
/// 具体流程见 <see cref="FileDownloader"/> 与 <see cref="BatchDownloader"/>。
/// </para>
/// </summary>
internal static class DownloadEngine
{
    /// <summary>
    /// 单个文件最多切成几块。
    ///
    /// 上限从 8 放宽到 16（1.0.9.8）：设置页现在把「分块数」独立暴露出来，
    /// 想给单个大文件提速的用户可以拉到 16。再高没有意义 ——
    /// 家用带宽早就跑满了，多出来的连接只会让镜像端更容易 403。
    /// </summary>
    private const int MinChunks = 1;
    private const int MaxChunks = 16;

    /// <summary>小于这个体积不分块 —— 分块握手（每个 range 一次请求）的开销会超过收益。</summary>
    private const long ChunkyThreshold = 1024 * 1024;

    /// <summary>单块最小体积，避免把 1 MB 切成 8 个 128 KB 的碎片。</summary>
    private const long MinChunkSize = 256 * 1024;

    /// <summary>
    /// 单块「多久没收到字节」算卡死（毫秒）。这是替代原来「单文件总时限」的关键 ——
    /// 慢而稳的大文件不会被误杀，真正挂住的连接 30 秒内就会被掐掉重试。
    /// </summary>
    private const int BlockTimeoutMs = 30_000;

    /// <summary>
    /// 单次 HTTP 请求的上限（毫秒）。放得比较宽：一个分块的响应体是边下边读的，
    /// 设太紧会让慢速链路上的大分块永远下不完（每次都超时重来，等于死循环）。
    /// </summary>
    private const int HttpClientTimeoutMs = 300_000;

    /// <summary>每块失败后自动重试次数（库内部），配合自动续传相当于「小块级断点续传」。</summary>
    private const int MaxTryAgainOnFailure = 3;

    /// <summary>
    /// 建一份针对单个文件的配置。
    /// </summary>
    /// <param name="chunks">
    /// 单个文件的分块数（= 内层并发连接数），由设置页的「单文件分块数」决定。
    ///
    /// <para>
    /// 允许 1：表示不分块、单连接下载。分块握手本身要对同一 URL 发 N 次 range 请求，
    /// 小文件或镜像端限流时，1 块反而更快、更不容易被 403。
    /// 上限 16 —— 再多连接对家用带宽没有收益，只会增加被限流的概率。
    /// </para>
    /// </param>
    internal static DownloadConfiguration CreateConfiguration(int chunks)
    {
        var count = Math.Clamp(chunks, MinChunks, MaxChunks);

        return new DownloadConfiguration
        {
            ChunkCount = count,
            // 只有 1 块时不能开并行：库会把它当成「单块 + 并行」去跑，
            // 结果是把同一段字节反复请求，还不如老老实实单连接流式读。
            ParallelDownload = count > 1,
            ParallelCount = count,
            EnableAutoResumeDownload = true,
            MaxTryAgainOnFailure = MaxTryAgainOnFailure,
            MinimumSizeOfChunking = ChunkyThreshold,
            MinimumChunkSize = MinChunkSize,
            BlockTimeout = BlockTimeoutMs,
            HttpClientTimeout = HttpClientTimeoutMs,
            // 50 MB 上限的内存缓冲：分块下载默认把收到的数据先放内存再落盘，
            // 不设上限时下几百 MB 的整合包会把内存顶起来。
            MaximumMemoryBufferBytes = 50L * 1024 * 1024,
            // 失败后清掉分块包数据（否则下次会拿着上次的残片「续传」到一个已损坏的文件）
            ClearPackageOnCompletionWithFailure = true,
            CheckDiskSizeBeforeDownload = false,
            // 交给自定义 handler：我们要自己控代理、连接池与解压策略
            CustomHttpMessageHandlerFactory = () => CreateHandler(count),
            RequestConfiguration =
            {
                Accept = "*/*",
                UserAgent = "VibrantbitLauncher/1.0",
                KeepAlive = true,
                ProtocolVersion = HttpVersion.Version11,
                // 关闭重定向上限放宽：BMCLAPI 的 /maven/ 会 302 到一堆志愿者节点，
                // 偶尔还要跳两三次才落到真实文件。
                AllowAutoRedirect = true,
                MaximumAutomaticRedirections = 20,
                // 必须显式给代理：HttpClient 默认只读 WinINet 系统代理，
                // 而很多用户只设了 HTTPS_PROXY 环境变量（Modrinth 那边同样这么处理）。
                Proxy = BuildProxy(),
            },
        };
    }

    /// <summary>
    /// 每个文件一份 handler。
    ///
    /// <para>
    /// <b>不能复用同一个实例</b>：库会给它套上 <c>new HttpClient(handler)</c>，
    /// 而 HttpClient 默认在 Dispose 时连带 Dispose handler —— 共享一份的话，
    /// 第一个文件下完就会把后面所有文件的连接池一起废掉。
    /// </para>
    /// </summary>
    private static HttpMessageHandler CreateHandler(int chunks)
    {
        return new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            UseProxy = true,
            Proxy = BuildProxy(),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // 连接池要容得下「这个文件的所有分块」，否则多出来的块会排队等连接，
            // 分块并行就退化成了串行。留一倍余量给同批次里别的文件。
            MaxConnectionsPerServer = Math.Max(16, chunks * 2),
            // 不要开 AutomaticDecompression：开了之后库会退化成单块下载
            // （压缩流的字节偏移和分块 range 对不上），见库的 README。
            AutomaticDecompression = DecompressionMethods.None,
        };
    }

    /// <summary>
    /// 选一个代理：优先 <c>HTTPS_PROXY</c>/<c>HTTP_PROXY</c> 环境变量，
    /// 否则交给系统默认代理（WinINet / WinHTTP 的设置）。
    /// </summary>
    private static IWebProxy? BuildProxy()
    {
        foreach (var key in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" })
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                return new WebProxy(uri);
        }

        return WebRequest.DefaultWebProxy;
    }
}
