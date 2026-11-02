using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Services;

/// <summary>一个体检项的结论。</summary>
public enum ProbeGrade
{
    /// <summary>通，且延迟正常。</summary>
    Ok,

    /// <summary>通，但明显偏慢（国内直连官方源的常见状态）。</summary>
    Slow,

    /// <summary>通，但慢到会拖住下载（几秒才出响应头）。</summary>
    VerySlow,

    /// <summary>没通。具体原因见 <see cref="NetworkProbeResult.Detail"/>。</summary>
    Failed,
}

/// <summary>要探测的一个地址。</summary>
/// <param name="Name">界面上的名字（中文原文，走翻译引擎）。</param>
/// <param name="Url">探测用的地址；TCP 类探测可以没有。</param>
/// <param name="Kind">探测类型。</param>
public sealed record ProbeTarget(string Name, string Url, ProbeKind Kind)
{
    /// <summary>TCP 探测的主机 + 端口（其它类型从 Url 推导）。</summary>
    public string? TcpHost { get; init; }

    /// <summary>TCP 探测的端口。</summary>
    public int TcpPort { get; init; }
}

/// <summary>探测方式：完整链路（DNS→TCP→HTTP）/ 只测 TCP 连通。</summary>
public enum ProbeKind
{
    Http,
    Tcp,
}

/// <summary>一个体检项的结果。</summary>
public sealed class NetworkProbeResult
{
    public required string Name { get; init; }

    public string Url { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    public bool Ok { get; init; }

    /// <summary>DNS 解析耗时（毫秒，-1 = 没测 / 失败）。</summary>
    public int DnsMs { get; init; } = -1;

    /// <summary>TCP 建连耗时（毫秒，-1 = 没到这一步）。</summary>
    public int ConnectMs { get; init; } = -1;

    /// <summary>从发请求到收到响应头的耗时（毫秒，-1 = 没到这一步）。</summary>
    public int HttpMs { get; init; } = -1;

    public int TotalMs { get; init; }

    public int StatusCode { get; init; }

    /// <summary>失败原因 / 附加说明（中文原文）。</summary>
    public string Detail { get; init; } = string.Empty;

    public ProbeGrade Grade { get; init; }

    /// <summary>界面上那一行结论。</summary>
    public string Verdict => Grade switch
    {
        ProbeGrade.Ok => "通畅",
        ProbeGrade.Slow => "偏慢",
        ProbeGrade.VerySlow => "很慢",
        _ => "不通",
    };

    /// <summary>耗时（毫秒）的展示串；没测到就给空。</summary>
    public string LatencyText => TotalMs <= 0 ? "—" : $"{TotalMs} ms";
}

/// <summary>
/// 网络体检：按「DNS → TCP → HTTP」三段分别计时，定位「到底慢在哪一段」。
///
/// <para>
/// <b>为什么要分段</b>：只报一个「连不上」对排查毫无用处 ——
/// DNS 被污染（解析到 0.0.0.0）、只封 443、TLS 握手被重置、HTTP 回 403，
/// 这四种情况在用户眼里都是「下载失败」，但处理办法完全不同。
/// </para>
///
/// <para>
/// 探测本身只读：GET 带 <c>Range: bytes=0-2047</c>，拿到响应头就把连接丢掉，
/// 不会真的把整个文件拖下来。
/// </para>
/// </summary>
public static class NetworkDiagnostics
{
    /// <summary>单个探测项的总时限：超了就判定这一项不通，继续下一项。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>DNS 单独给一个时限（有的网络 DNS 会挂很久）。</summary>
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(5);

    /// <summary>判定「偏慢」与「很慢」的分界（毫秒）。</summary>
    private const int SlowThreshold = 600;
    private const int VerySlowThreshold = 2000;

    private static HttpClient? _shared;

    /// <summary>
    /// 体检用的 HttpClient。刻意**不复用下载器的 handler**：
    /// 那个 handler 关了自动解压、连接池也按分块数放大，对一次只读 2 KB 的探测没有意义。
    /// </summary>
    private static HttpClient Client => _shared ??= new HttpClient(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(6),
        UseProxy = true,
        Proxy = DownloadEngine.BuildProxy(),
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 8,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// 本次要体检的清单：官方源、镜像、模组站、加载器 maven、联机中继，加上用户自定义的源。
    ///
    /// 顺序刻意按「装一个整合包会依次碰到谁」排：先元数据、再依赖、最后是玩家内容。
    /// </summary>
    public static IReadOnlyList<ProbeTarget> BuildTargets()
    {
        var targets = new List<ProbeTarget>
        {
            new("官方版本清单", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ProbeKind.Http),
            new("官方资源文件", "https://resources.download.minecraft.net/", ProbeKind.Http),
            new("BMCLAPI 镜像", DownloadSources.BmclMirrorRoot + "/version/1.20.1/json", ProbeKind.Http),
            new("Fabric 依赖库", "https://maven.fabricmc.net/net/fabricmc/fabric-loader/maven-metadata.xml", ProbeKind.Http),
            new("Forge 依赖库", "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ProbeKind.Http),
            new("NeoForge 依赖库", "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ProbeKind.Http),
            new("Modrinth 模组站", "https://api.modrinth.com/v2/tag/loader", ProbeKind.Http),
            new("GitHub（联机内核下载）", "https://api.github.com/rate_limit", ProbeKind.Http),
        };

        // 自定义源：只有用户真的填了才测，免得凭空多出几项红色。
        foreach (var root in DownloadSources.CustomMirrorRoots)
            targets.Add(new($"自定义源 {DownloadSources.HostOf(root)}", root + "/version/1.20.1/json", ProbeKind.Http));

        // 联机中继：TUN 建立后走的是 UDP/TCP 到中继，只测 TCP 能不能连上。
        if (TryParseHostPort(SettingsService.Current.EasyTierRelayServer, out var relayHost, out var relayPort))
        {
            targets.Add(new("联机中继服务器", string.Empty, ProbeKind.Tcp)
            {
                TcpHost = relayHost,
                TcpPort = relayPort,
            });
        }

        return targets;
    }

    /// <summary>逐项体检。<paramref name="progress"/> 每测完一项回调一次。</summary>
    public static async Task<IReadOnlyList<NetworkProbeResult>> RunAsync(
        IProgress<NetworkProbeResult>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<NetworkProbeResult>();
        foreach (var target in BuildTargets())
        {
            if (ct.IsCancellationRequested)
                break;

            var result = await ProbeAsync(target, ct).ConfigureAwait(false);
            results.Add(result);
            progress?.Report(result);
        }

        return results;
    }

    /// <summary>体检单项。任何异常都收敛成一条「不通」的结果，绝不外抛。</summary>
    public static async Task<NetworkProbeResult> ProbeAsync(ProbeTarget target, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        var host = target.TcpHost ?? HostOnly(target.Url);
        var port = target.TcpPort;

        // 端口以 URL 里写的为准（自定义源常常带 :8080 这种），没写才按协议取默认值
        if (port <= 0 && Uri.TryCreate(target.Url, UriKind.Absolute, out var probeUri))
            port = probeUri.Port;

        if (port <= 0)
            port = target.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 80 : 443;

        if (string.IsNullOrWhiteSpace(host))
        {
            return new NetworkProbeResult
            {
                Name = target.Name,
                Ok = false,
                Grade = ProbeGrade.Failed,
                Detail = "地址格式不对，没有可解析的主机名",
            };
        }

        var watch = Stopwatch.StartNew();

        // ① DNS：解析失败通常是 DNS 污染 / 断网，和「墙」是两回事，必须单独报
        var dnsWatch = Stopwatch.StartNew();
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dnsCts.CancelAfter(DnsTimeout);
            var addresses = await Dns.GetHostAddressesAsync(host, dnsCts.Token).ConfigureAwait(false);
            if (addresses.Length == 0)
                throw new SocketException((int)SocketError.HostNotFound);
            dnsWatch.Stop();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            dnsWatch.Stop();
            var timedOut = ex is OperationCanceledException;
            return new NetworkProbeResult
            {
                Name = target.Name,
                Url = target.Url,
                Host = host,
                Ok = false,
                Grade = ProbeGrade.Failed,
                DnsMs = (int)dnsWatch.ElapsedMilliseconds,
                TotalMs = (int)watch.ElapsedMilliseconds,
                Detail = timedOut ? "DNS 解析超时（DNS 服务器无响应）" : "DNS 解析失败（域名查不到地址）",
            };
        }

        // ② TCP：能连上说明网络路径通，剩下的问题都在应用层
        var connectWatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            connectWatch.Stop();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            connectWatch.Stop();
            return new NetworkProbeResult
            {
                Name = target.Name,
                Url = target.Url,
                Host = host,
                Ok = false,
                Grade = ProbeGrade.Failed,
                DnsMs = (int)dnsWatch.ElapsedMilliseconds,
                ConnectMs = (int)connectWatch.ElapsedMilliseconds,
                TotalMs = (int)watch.ElapsedMilliseconds,
                Detail = ex is OperationCanceledException
                    ? $"连接 {host}:{port} 超时（端口被拦或线路不通）"
                    : $"连接 {host}:{port} 被拒绝",
            };
        }

        var tcpMs = (int)connectWatch.ElapsedMilliseconds;

        // 只测 TCP 的项目到此为止
        if (target.Kind == ProbeKind.Tcp || string.IsNullOrWhiteSpace(target.Url))
        {
            watch.Stop();
            return new NetworkProbeResult
            {
                Name = target.Name,
                Url = target.Url,
                Host = host,
                Ok = true,
                Grade = Grade(tcpMs),
                DnsMs = (int)dnsWatch.ElapsedMilliseconds,
                ConnectMs = tcpMs,
                TotalMs = (int)watch.ElapsedMilliseconds,
                Detail = "端口可连通",
            };
        }

        // ③ HTTP：只读前 2 KB，测的是「多久看到响应头」
        var httpWatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
            request.Headers.Range = new RangeHeaderValue(0, 2047);
            request.Headers.UserAgent.ParseAdd("VibrantbitLauncher/1.0 (+diagnostics)");

            using var response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            httpWatch.Stop();
            watch.Stop();

            var status = (int)response.StatusCode;

            // 4xx 也算链路正常：根路径 403/404 只说明这个地址不对外列目录，
            // 真正的下载地址在别的路径上。只有 5xx 才说明对端自己出问题了。
            if (status >= 500)
            {
                return new NetworkProbeResult
                {
                    Name = target.Name,
                    Url = target.Url,
                    Host = host,
                    Ok = false,
                    Grade = ProbeGrade.Failed,
                    DnsMs = (int)dnsWatch.ElapsedMilliseconds,
                    ConnectMs = tcpMs,
                    HttpMs = (int)httpWatch.ElapsedMilliseconds,
                    TotalMs = (int)watch.ElapsedMilliseconds,
                    StatusCode = status,
                    Detail = $"服务器自己报错（HTTP {status}）",
                };
            }

            var total = (int)watch.ElapsedMilliseconds;
            return new NetworkProbeResult
            {
                Name = target.Name,
                Url = target.Url,
                Host = host,
                Ok = true,
                Grade = Grade(total),
                DnsMs = (int)dnsWatch.ElapsedMilliseconds,
                ConnectMs = tcpMs,
                HttpMs = (int)httpWatch.ElapsedMilliseconds,
                TotalMs = total,
                StatusCode = status,
                Detail = status is >= 400 ? $"HTTP {status}（能连上，但该路径不可访问）" : string.Empty,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            httpWatch.Stop();
            watch.Stop();

            var detail = ex switch
            {
                OperationCanceledException => "HTTPS 请求超时（握手或响应太慢）",
                HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } =>
                    "TLS 握手失败（证书或协议被中间人干扰）",
                HttpRequestException hre when hre.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) =>
                    "TLS 握手失败（证书或协议被中间人干扰）",
                _ => "HTTPS 请求失败（连接被重置或代理配置有问题）",
            };

            return new NetworkProbeResult
            {
                Name = target.Name,
                Url = target.Url,
                Host = host,
                Ok = false,
                Grade = ProbeGrade.Failed,
                DnsMs = (int)dnsWatch.ElapsedMilliseconds,
                ConnectMs = tcpMs,
                HttpMs = (int)httpWatch.ElapsedMilliseconds,
                TotalMs = (int)watch.ElapsedMilliseconds,
                Detail = detail,
            };
        }
    }

    /// <summary>
    /// 从 URL 里取**不带端口**的主机名。
    ///
    /// <see cref="DownloadSources.HostOf"/> 会把 <c>host:port</c> 一起留下（日志里那样更好读），
    /// 但拿去做 DNS 解析就会失败 —— 本地自测为这个坑付过一次代价。
    /// </summary>
    private static string HostOnly(string url)
    {
        var host = DownloadSources.HostOf(url);
        if (string.IsNullOrEmpty(host))
            return host;

        // IPv6 字面量带方括号：[::1]:8080 → ::1
        if (host[0] == '[')
        {
            var end = host.IndexOf(']');
            return end > 0 ? host[1..end] : host;
        }

        var colon = host.IndexOf(':');
        return colon < 0 ? host : host[..colon];
    }

    /// <summary>按总耗时分级。</summary>
    private static ProbeGrade Grade(int totalMs) =>
        totalMs >= VerySlowThreshold ? ProbeGrade.VerySlow
        : totalMs >= SlowThreshold ? ProbeGrade.Slow
        : ProbeGrade.Ok;

    /// <summary>
    /// 从「主机:端口」或 <c>tcp://主机:端口</c> 里拆出主机与端口。
    /// 解析不出来就不测这一项（中继地址允许带协议前缀）。
    /// </summary>
    private static bool TryParseHostPort(string? raw, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            text = text[(scheme + 3)..];

        var slash = text.IndexOf('/');
        if (slash >= 0)
            text = text[..slash];

        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
            return false;

        host = text[..colon];
        return int.TryParse(text[(colon + 1)..], out port) && port is > 0 and <= 65535;
    }
}
