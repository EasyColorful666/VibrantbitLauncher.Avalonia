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

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            try { client.DefaultRequestHeaders.UserAgent.ParseAdd("VibrantbitLauncher/1.0"); } catch { }
            return client;
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

            var json = await Http.GetStringAsync(url, cancellationToken);
            var response = JsonSerializer.Deserialize<SearchResponse>(json);
            return new SearchResult
            {
                Hits = response?.Hits ?? new List<Hit>(),
                TotalHits = response?.TotalHits ?? 0
            };
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
}