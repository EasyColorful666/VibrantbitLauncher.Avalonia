using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 公共中继地址解析：把「接口地址」换成「真正的服务器地址」。
///
/// <para>
/// 陶瓦联机给的那个 <c>https://etnode.zkitefly.eu.org/node1</c> 并不是一台能直连的服务器，
/// 而是一个**发地址的接口**：GET 一下就返回一行 <c>tcp://1.2.3.4:11010</c>，
/// 那才是这轮真正该连的节点（节点会换 IP / 换端口，所以地址不能写死）。
/// </para>
///
/// <para>
/// 这里自己取一次，而不是把接口地址原样交给核心，好处有三：
/// 取不到能立刻告诉玩家「节点接口不通」，而不是让核心挂着干等；
/// 取到的真地址能显示在界面上，连不上时一眼看出连的是谁；
/// 顺带把同一批里的坏节点（比如已经下线、只回跳转的）直接筛掉，不给核心白试。
/// </para>
///
/// <para>
/// 返回值约定：非 <c>http(s)</c> 的地址原样返回；接口取不到时返回空，
/// 由调用方决定回退 —— 这里不替上层做「悄悄换个节点」的决定。
/// 成功结果缓存 10 分钟，避免反复开服把接口打进限流。
/// </para>
/// </summary>
internal static class PublicRelayResolver
{
    /// <summary>从返回内容里抠出所有可直连的地址（纯文本一行、或多行、或 JSON 都能覆盖）。</summary>
    private static readonly Regex AddressPattern = new(
        "(?:tcp|udp|ws|wss)://[^\\s\"'<>,\\\\]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 单次请求上限。
    ///
    /// 这个接口经常要 3~10 秒才回（实测同一台机器连着请求，也有一半直接挂到超时），
    /// 所以单次给 6 秒、失败再来一次：两次都空才认定「接口不可用」，
    /// 总耗时封在 12 秒，比吊着不放让玩家以为卡死要好。
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    /// <summary>失败重试次数（不含首次）。</summary>
    private const int RetryCount = 1;

    private sealed record CacheEntry(DateTimeOffset ExpireAt, IReadOnlyList<string> Addresses);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>
    /// 把一串候选中继逐个解析成真地址，按原顺序去重。
    ///
    /// 接口地址取不到就跳过（不是中断），这样「接口挂了 + 还有直连节点」时仍然能联机。
    /// </summary>
    public static async Task<IReadOnlyList<string>> ResolveAllAsync(
        IEnumerable<string?> relays,
        CancellationToken cancellationToken = default)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relay in relays)
        {
            var resolved = await ResolveAsync(relay, cancellationToken).ConfigureAwait(false);

            foreach (var address in resolved)
            {
                if (seen.Add(address))
                    result.Add(address);
            }
        }

        return result;
    }

    /// <summary>解析单个中继：是接口就去要地址，是直连地址就原样返回。</summary>
    public static async Task<IReadOnlyList<string>> ResolveAsync(
        string? relay,
        CancellationToken cancellationToken = default)
    {
        var value = (relay ?? string.Empty).Trim();
        if (value.Length == 0)
            return Array.Empty<string>();

        if (!IsEndpointApi(value))
            return new[] { value };

        if (Cache.TryGetValue(value, out var cached))
        {
            if (cached.ExpireAt > DateTimeOffset.UtcNow)
                return cached.Addresses;

            // 过期了但接口这会儿正好不通 —— 用上次拿到的地址，总比连不上强
            var refreshed = await FetchAsync(value, cancellationToken).ConfigureAwait(false);
            if (refreshed is null || refreshed.Count == 0)
            {
                Serilog.Log.Warning("公共中继接口刷新失败，沿用上次的地址：{Url}", value);
                return cached.Addresses;
            }

            Cache[value] = new CacheEntry(DateTimeOffset.UtcNow + CacheLifetime, refreshed);
            return refreshed;
        }

        var addresses = await FetchAsync(value, cancellationToken).ConfigureAwait(false);

        if (addresses is null)
            return Array.Empty<string>();

        if (addresses.Count > 0)
            Cache[value] = new CacheEntry(DateTimeOffset.UtcNow + CacheLifetime, addresses);

        return addresses;
    }

    /// <summary>
    /// 取一次地址，失败自动重试。
    ///
    /// 返回值用 null 和空列表区分两种结局：null = 接口没通（可以重试、也可以让上层回退），
    /// 空列表 = 接口通了但里面没有地址（重试没意义）。
    /// </summary>
    private static async Task<IReadOnlyList<string>?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= RetryCount; attempt++)
        {
            var addresses = await FetchOnceAsync(url, cancellationToken).ConfigureAwait(false);
            if (addresses is not null)
                return addresses;

            if (attempt < RetryCount)
            {
                Serilog.Log.Information("公共中继接口没通，重试一次：{Url}", url);

                try
                {
                    await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>?> FetchOnceAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            // 必须锁 HTTP/1.1：这个节点走 Cloudflare，.NET 默认协商到 h2 时会一直挂着不返回，
            // 直到超时（实测 h2 直连超时、强制 1.1 能回）。反正响应只有一行地址，用不着多路复用。
            using var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var addresses = Extract(body);

            Serilog.Log.Information("公共中继接口 {Url} 返回 {Count} 个地址：{Addresses}",
                url, addresses.Count, string.Join(" | ", addresses));

            return addresses;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "读取公共中继接口失败：{Url}", url);
            return null;
        }
    }

    /// <summary>
    /// 抠地址。不假设返回格式：陶瓦的接口现在回一行纯文本，
    /// 将来改成 JSON 数组也只是多几个引号，这条正则都吃得下。
    /// </summary>
    private static IReadOnlyList<string> Extract(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return Array.Empty<string>();

        var list = new List<string>();

        foreach (Match match in AddressPattern.Matches(body))
        {
            // 末尾可能挂着句子标点（比如「地址是 tcp://1.2.3.4:11010。」）
            var address = match.Value.TrimEnd('.', ')', ']', ';', '。');
            if (address.Length > 0)
                list.Add(address);
        }

        return list;
    }

    private static bool IsEndpointApi(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateHttp()
    {
        // 超时统一由 CancellationTokenSource 控制，避免 HttpClient 自带超时和它打架
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        // 少了 UA 会被部分站点直接挡掉
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            $"VibrantbitLauncher/{typeof(PublicRelayResolver).Assembly.GetName().Version}");

        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/plain, */*");

        return client;
    }
}
