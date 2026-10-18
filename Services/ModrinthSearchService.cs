using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// Modrinth 搜索。
    ///
    /// 自己实现而不用 MinecraftLaunch 的 ModrinthProvider：后者的 SearchAsync 只有
    /// searchFilter / version / modLoader 三个参数，写死了 mod 类型，无法查整合包、
    /// 资源包和光影。这里直接走 Modrinth 的 search 接口，用 facets 指定 project_type。
    /// 接口文档：https://docs.modrinth.com/api/operations/searchprojects/
    /// </summary>
    public static class ModrinthSearchService
    {
        public const string TypeMod = "mod";
        public const string TypeModpack = "modpack";
        public const string TypeResourcePack = "resourcepack";
        public const string TypeShader = "shader";
        /// <summary>数据包：Modrinth 里不是一个独立 project_type，而是一组项目的复合标签。</summary>
        public const string TypeDatapack = "datapack";

        /// <summary>Modrinth 的排序方式：索引 = relevance / downloads / follows / newest / updated。</summary>
        public static readonly string[] SortIndexes = { "relevance", "downloads", "follows", "newest", "updated" };

        /// <summary>单次请求超时。Modrinth 走 Cloudflare，正常 1~2 秒内返回，20 秒已经是很宽的余量。</summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

        /// <summary>失败后的重试轮数。国内访问 api.modrinth.com 偶发握手被重置，重试一两次多半能过。</summary>
        private const int MaxAttempts = 3;

        private static readonly HttpClient Http = CreateClient();

        /// <summary>
        /// 建 HttpClient。
        ///
        /// <para>
        /// <b>必须显式启用系统代理</b>：<c>api.modrinth.com</c> 在国内多数线路不可直连，
        /// 用户普遍靠代理访问。而 HttpClient 默认的 <c>UseProxy = true</c> 走的是
        /// **WinHTTP/WinINet 的系统代理**，环境变量形式的代理（很多工具只设这个）读不到。
        /// 这里显式挂一个 <see cref="WebRequest.DefaultWebProxy"/> 之外还会尝试读
        /// <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c> 环境变量，两条路都覆盖到。
        /// </para>
        ///
        /// <para>
        /// 关掉 HttpClient 自带总超时（<c>InfiniteTimeSpan</c>），改由每次请求的 CTS 控时 ——
        /// 默认 100 秒会让「连不上」的场景干等太久。
        /// </para>
        /// </summary>
        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
                UseProxy = true,
                Proxy = BuildProxy(),
                PooledConnectionLifetime = TimeSpan.FromMinutes(3),
                MaxConnectionsPerServer = 8,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            };

            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            try { client.DefaultRequestHeaders.UserAgent.ParseAdd("VibrantbitLauncher/1.0"); } catch { }
            return client;
        }

        /// <summary>
        /// 选一个代理：优先 <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c> 环境变量（部分工具只设这个，
        /// 系统代理里看不到），否则交给系统默认代理。
        /// </summary>
        private static System.Net.IWebProxy? BuildProxy()
        {
            foreach (var key in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" })
            {
                var raw = Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                    return new System.Net.WebProxy(uri);
            }

            // 交给系统默认（WinInet / WinHTTP 的代理设置）
            return System.Net.WebRequest.DefaultWebProxy;
        }

        /// <summary>
        /// 把网络异常翻成一句给用户看的话。
        /// 需要在正式文案里点出「Modrinth 在国内通常需要代理」，否则用户只会看到「没有匹配的资源」。
        /// </summary>
        public static string DescribeError(Exception? ex)
        {
            if (ex is null)
                return "未知错误";

            if (ex is OperationCanceledException or TimeoutException || ex.InnerException is TimeoutException)
                return "连接 Modrinth 超时";

            if (ex is HttpRequestException httpEx)
            {
                if (httpEx.StatusCode.HasValue)
                {
                    var code = (int)httpEx.StatusCode.Value;
                    return code switch
                    {
                        429 => "请求过于频繁（429），请稍后重试",
                        >= 500 => $"Modrinth 服务器返回 {code}（服务端故障，稍后重试）",
                        403 => "Modrinth 拒绝了本次请求（403），可能是所在网络被限制",
                        _ => $"Modrinth 返回 {code}",
                    };
                }

                return "无法连接 Modrinth";
            }

            return ex.Message;
        }

        public sealed class Hit
        {
            [JsonPropertyName("project_id")] public string ProjectId { get; set; } = string.Empty;
            [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
            [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
            [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
            [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
            [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
            [JsonPropertyName("downloads")] public long Downloads { get; set; }
            [JsonPropertyName("follows")] public long Follows { get; set; }
            [JsonPropertyName("date_modified")] public DateTimeOffset? DateModified { get; set; }
            [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();
            [JsonPropertyName("display_categories")] public List<string> DisplayCategories { get; set; } = new();
            [JsonPropertyName("versions")] public List<string> Versions { get; set; } = new();
        }

        private sealed class SearchResponse
        {
            [JsonPropertyName("hits")] public List<Hit> Hits { get; set; } = new();
            /// <summary>命中总数（不是本页条数），用于「共 N 条」计数文案。</summary>
            [JsonPropertyName("total_hits")] public long TotalHits { get; set; }
        }

        /// <summary>一页搜索结果：本页条目 + 命中总数。</summary>
        public sealed class SearchResult
        {
            public List<Hit> Hits { get; init; } = new();
            public long TotalHits { get; init; }
        }

        /// <summary>
        /// 按项目类型搜索；gameVersion / loader / category 传 null 或 "全部" 表示不筛选。
        /// sortIndex 取 <see cref="SortIndexes"/> 中的值，<c>index</c> 参数直接透传给 Modrinth。
        /// </summary>
        public static Task<SearchResult> SearchAsync(
            string projectType,
            string? query = null,
            string? gameVersion = null,
            string? loader = null,
            string? category = null,
            int limit = 20,
            string? sortIndex = null,
            CancellationToken cancellationToken = default)
            => SearchByFacetsAsync(ToFacetGroup(projectType), query, gameVersion, loader, category, limit, sortIndex, 0, cancellationToken);

        /// <summary>
        /// 按项目类型搜索的一页结果（支持分页偏移）。
        /// <paramref name="offset"/> 用于「滚动到底加载下一页」：Modrinth 的 offset 参数直接透传。
        /// </summary>
        public static Task<SearchResult> SearchAsync(
            string projectType,
            string? query,
            string? gameVersion,
            string? loader,
            string? category,
            int limit,
            string? sortIndex,
            int offset,
            CancellationToken cancellationToken = default)
            => SearchByFacetsAsync(ToFacetGroup(projectType), query, gameVersion, loader, category, limit, sortIndex, offset, cancellationToken);

        /// <summary>
        /// 底层重载：直接给一组 facet 组（外层 OR、内层 AND）。
        ///
        /// 之所以要暴露它，是因为「数据包」在 Modrinth 上没有 project_type:datapack ——
        /// 数据包是项目身上的一个 category，项目本身的 project_type 仍是 mod / plugin。
        /// 于是它需要 <c>["datapack"]</c> 与 <c>["project_type:mod","project_type:plugin"]</c>
        /// 两个组（组间 OR）来近似表达。
        /// </summary>
        public static async Task<SearchResult> SearchByFacetsAsync(
            IReadOnlyList<string> facetGroups,
            string? query = null,
            string? gameVersion = null,
            string? loader = null,
            string? category = null,
            int limit = 20,
            string? sortIndex = null,
            int offset = 0,
            CancellationToken cancellationToken = default)
        {
            var facets = new List<string>(facetGroups);

            if (!string.IsNullOrWhiteSpace(gameVersion) && gameVersion != "全部")
                facets.Add($"[\"versions:{gameVersion}\"]");

            if (!string.IsNullOrWhiteSpace(loader) && loader != "全部")
                facets.Add($"[\"categories:{Escape(loader)}\"]");

            if (!string.IsNullOrWhiteSpace(category) && category != "全部")
                facets.Add($"[\"categories:{Escape(category)}\"]");

            if (limit <= 0)
                limit = 20;

            if (offset < 0)
                offset = 0;

            var facetsJson = Uri.EscapeDataString("[" + string.Join(",", facets) + "]");
            var url = $"https://api.modrinth.com/v2/search?limit={limit}&facets={facetsJson}";

            if (offset > 0)
                url += $"&offset={offset}";

            if (!string.IsNullOrWhiteSpace(query))
                url += $"&query={Uri.EscapeDataString(query)}";

            if (!string.IsNullOrWhiteSpace(sortIndex) && sortIndex != "relevance")
                url += $"&index={Uri.EscapeDataString(sortIndex)}";

            // 重试：Modrinth 走 Cloudflare，国内线路偶发 TLS 握手被重置 / 读到半截响应体，
            // 这类失败几乎都是瞬时的，重试一两次就过。原来一次失败就让整页空掉，
            // 表现正是用户报的「资源一直加载不出来」。
            Exception? last = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    try { await Task.Delay(300 * attempt, cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                }

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(RequestTimeout);

                    using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var status = resp.StatusCode;
                        throw new HttpRequestException(
                            $"HTTP {(int)status} {status}", inner: null, statusCode: status);
                    }

                    var json = await resp.Content.ReadAsStringAsync(cts.Token);
                    var response = JsonSerializer.Deserialize<SearchResponse>(json);
                    return new SearchResult
                    {
                        Hits = response?.Hits ?? new List<Hit>(),
                        TotalHits = response?.TotalHits ?? 0
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    last = ex;

                    // 4xx（除 429）是请求本身的问题，重试没有意义
                    if (ex is HttpRequestException { StatusCode: not null } h
                        && (int)h.StatusCode.Value is >= 400 and < 500 and not 429)
                        break;

                    Serilog.Log.Warning(ex, "Modrinth 搜索失败（第 {Attempt}/{Max} 次）：{Url}", attempt, MaxAttempts, url);
                }
            }

            throw new ModrinthException(
                $"无法访问 Modrinth（{DescribeError(last)}）。"
                + "Modrinth 服务器在境外，国内网络通常需要开启代理，"
                + "或在「设置 → 下载」中确认代理配置后重试。",
                last);
        }

        /// <summary>
        /// 项目类型 → facet 组（外层 OR、内层 AND）。
        ///
        /// 注意 project_type 的合法取值只有 mod / modpack / resourcepack / shader
        /// （见 https://docs.modrinth.com/api/operations/searchprojects/），
        /// datapack 只是 all_project_types 与 categories 里的值，写成
        /// <c>["project_type:datapack"]</c> 会被接口判为 400。
        /// 因此「数据包」用 <c>["categories:datapack"]</c> 表达。
        /// </summary>
        private static string[] ToFacetGroup(string projectType) => projectType switch
        {
            TypeMod => new[] { "[\"project_type:mod\"]" },
            TypeModpack => new[] { "[\"project_type:modpack\"]" },
            TypeResourcePack => new[] { "[\"project_type:resourcepack\"]" },
            TypeShader => new[] { "[\"project_type:shader\"]" },
            // 数据包：project_type 无此值，走 categories 过滤
            TypeDatapack => new[] { "[\"categories:datapack\"]" },
            _ => new[] { "[\"project_type:mod\"]" }
        };

        /// <summary>facet 值里的引号/反斜杠需要转义，否则拼出来的 JSON 数组会被 Modrinth 判为非法。</summary>
        private static string Escape(string value)
            => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// 访问 Modrinth 失败时抛出。
    ///
    /// 单独一个类型是为了让上层能区分「Modrinth 连不上」与「本地文件/其它错误」，
    /// 从而给出不同的提示（连不上 → 引导开代理；没有任何理由指向本地磁盘或下载源）。
    /// </summary>
    public class ModrinthException : Exception
    {
        public ModrinthException(string message, Exception? inner) : base(message, inner) { }
    }
}