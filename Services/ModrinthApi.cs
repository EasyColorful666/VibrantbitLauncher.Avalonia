using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VibrantbitLauncher.Services;

/// <summary>
/// Modrinth 的「项目 / 版本 / 哈希」三组查询。
///
/// <para>
/// 与 <see cref="ModrinthSearchService"/>（只做搜索）分开，是因为这两类需求的失败语义不同：
/// 搜索失败要引导用户「开代理再试」，而这里的调用多数发生在**自动补全依赖 / 解析分享码**的
/// 流程里，失败应当降级成「这个文件找不到下载地址、跳过」，而不是让整个流程崩掉。
/// 因此这里的每个方法都不抛 <see cref="ModrinthException"/>，失败一律返回 null / 空表。
/// </para>
///
/// <para>
/// 代理处理与 <see cref="ModrinthSearchService"/> 保持一致（环境变量 + 系统代理两条路），
/// 因为 <c>api.modrinth.com</c> 在国内多数线路必须走代理。
/// </para>
/// </summary>
public static class ModrinthApi
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = true,
            Proxy = BuildProxy(),
            PooledConnectionLifetime = TimeSpan.FromMinutes(3),
            MaxConnectionsPerServer = 8,
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try { client.DefaultRequestHeaders.UserAgent.ParseAdd("VibrantbitLauncher/1.0 (dependency-resolver)"); } catch { }
        return client;
    }

    private static IWebProxy BuildProxy()
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

    // ==================== 数据模型 ====================

    /// <summary>Modrinth 项目（搜索结果里的精简视图）。</summary>
    public sealed class ProjectInfo
    {
        public string Slug { get; init; } = string.Empty;
        public string ProjectId { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public long Downloads { get; init; }
    }

    /// <summary>Modrinth 的一个发布版本。</summary>
    public sealed class VersionInfo
    {
        public string Id { get; init; } = string.Empty;
        public string ProjectId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string VersionNumber { get; init; } = string.Empty;
        public List<string> GameVersions { get; init; } = new();
        public List<string> Loaders { get; init; } = new();
        public List<FileInfo> Files { get; init; } = new();

        /// <summary>主文件（Modrinth 标了 <c>primary</c> 的那个；没有则取第一个）。</summary>
        public FileInfo? PrimaryFile =>
            Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();
    }

    /// <summary>版本里的一个文件。</summary>
    public sealed class FileInfo
    {
        public string Url { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string Sha1 { get; init; } = string.Empty;
        public long Size { get; init; }
        public bool Primary { get; init; }
    }

    // ==================== 查询 ====================

    /// <summary>
    /// 按 mod id（jar 里声明的那个，通常与 Modrinth 的 slug 同名）找最匹配的项目。
    /// 先精确匹配 slug / title，退化时取下载量最高的那条（人气通常就是「正规的那个」）。
    /// </summary>
    public static async Task<ProjectInfo?> FindProjectByModIdAsync(
        string modId, string? loader, string? gameVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modId))
            return null;

        var facets = new List<string> { "[\"project_type:mod\"]" };
        var p = ModLoaderHelperRef.NormalizeLoader(loader);
        if (p is not null)
            facets.Add($"[\"categories:{p}\"]");
        if (!string.IsNullOrWhiteSpace(gameVersion))
            facets.Add($"[\"versions:{gameVersion}\"]");

        var url = $"https://api.modrinth.com/v2/search?limit=8&query={Uri.EscapeDataString(modId)}"
                  + $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

        var doc = await GetJsonAsync(url, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("hits", out var hits)
            || hits.ValueKind != JsonValueKind.Array)
            return null;

        var candidates = new List<ProjectInfo>();
        foreach (var h in hits.EnumerateArray())
        {
            candidates.Add(new ProjectInfo
            {
                ProjectId = Str(h, "project_id"),
                Slug = Str(h, "slug"),
                Title = Str(h, "title"),
                Description = Str(h, "description"),
                Downloads = h.TryGetProperty("downloads", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetInt64() : 0,
            });
        }

        if (candidates.Count == 0)
            return null;

        // 精确匹配优先（slug / title 与 mod id 相等，忽略大小写与连字符差异）
        var key = Normalize(modId);
        var exact = candidates.FirstOrDefault(c => Normalize(c.Slug) == key || Normalize(c.Title) == key);
        return exact ?? candidates.OrderByDescending(c => c.Downloads).First();
    }

    /// <summary>
    /// 取某项目在指定加载器 / 游戏版本下的版本列表。
    /// 两个筛选为空时不筛，返回最新在前（Modrinth 默认按发布时间倒序）。
    /// </summary>
    public static async Task<List<VersionInfo>> GetProjectVersionsAsync(
        string projectIdOrSlug,
        string? loader = null,
        string? gameVersion = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectIdOrSlug))
            return new List<VersionInfo>();

        var query = new List<string>();
        var l = ModLoaderHelperRef.NormalizeLoader(loader);
        if (l is not null)
            query.Add($"loaders={Uri.EscapeDataString($"[\"{l}\"]")}");
        if (!string.IsNullOrWhiteSpace(gameVersion))
            query.Add($"game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}");

        var url = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectIdOrSlug)}/version";
        if (query.Count > 0)
            url += "?" + string.Join("&", query);

        var doc = await GetJsonAsync(url, ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
            return new List<VersionInfo>();

        var result = new List<VersionInfo>();
        foreach (var v in doc.RootElement.EnumerateArray())
            result.Add(ParseVersion(v));
        return result;
    }

    /// <summary>
    /// 按文件 SHA1 反查它属于哪个 Modrinth 版本。
    /// 这是 Modrinth 官方给启动器用的接口：本地已有的 jar 不必靠文件名猜，直接按哈希认。
    /// 命中后会跟随 302 到项目的 slug 版本。
    /// </summary>
    public static async Task<VersionInfo?> GetVersionByHashAsync(string sha1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sha1))
            return null;

        var doc = await GetJsonAsync(
            $"https://api.modrinth.com/v2/version_file/{Uri.EscapeDataString(sha1)}?algorithm=sha1", ct);

        return doc is null || doc.RootElement.ValueKind != JsonValueKind.Object
            ? null
            : ParseVersion(doc.RootElement);
    }

    // ==================== 解析 ====================

    private static VersionInfo ParseVersion(JsonElement v)
    {
        var files = new List<FileInfo>();
        if (v.TryGetProperty("files", out var fs) && fs.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fs.EnumerateArray())
            {
                var sha1 = string.Empty;
                if (f.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
                    sha1 = Str(hashes, "sha1");

                files.Add(new FileInfo
                {
                    Url = Str(f, "url"),
                    FileName = Str(f, "filename"),
                    Sha1 = sha1,
                    Size = f.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
                    Primary = f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True,
                });
            }
        }

        return new VersionInfo
        {
            Id = Str(v, "id"),
            ProjectId = Str(v, "project_id"),
            Name = Str(v, "name"),
            VersionNumber = Str(v, "version_number"),
            GameVersions = StrArray(v, "game_versions"),
            Loaders = StrArray(v, "loaders"),
            Files = files,
        };
    }

    // ==================== 网络 ====================

    private static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RequestTimeout);

                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    // 404 是「这个哈希 / 项目不存在」，重试没有意义
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        return null;
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
                }

                var json = await resp.Content.ReadAsStringAsync(cts.Token);
                return JsonDocument.Parse(json);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == 3)
                {
                    // 手动补全依赖是「锦上添花」的功能，连不上就静默放弃，不要打扰用户
                    Serilog.Log.Debug(ex, "Modrinth API 请求失败：{Url}", url);
                    return null;
                }

                try { await Task.Delay(300 * attempt, ct); } catch (OperationCanceledException) { throw; }
            }
        }

        return null;
    }

    private static string Str(JsonElement obj, string prop)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(prop, out var el)
           && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    private static List<string> StrArray(JsonElement obj, string prop)
    {
        var list = new List<string>();
        if (obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty(prop, out var el)
            || el.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString() ?? string.Empty);

        return list;
    }

    private static string Normalize(string s)
        => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}

/// <summary>
/// 把加载器「展示名」翻译成 Modrinth 的 facet 值。
/// 单独放一个内部类是为了不把 <see cref="ModLoaderHelper"/> 从 Helpers 命名空间拖进来 ——
/// ModrinthApi 在 Services 命名空间，直接引用会让这个文件的依赖方向变乱。
/// </summary>
internal static class ModLoaderHelperRef
{
    /// <summary>已知的加载器展示名 → Modrinth facet 值（小写）。认不出来返回 null（不筛选）。</summary>
    public static string? NormalizeLoader(string? loader)
    {
        if (string.IsNullOrWhiteSpace(loader))
            return null;

        var key = new string(loader.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return key switch
        {
            "fabric" => "fabric",
            "quilt" => "quilt",
            "forge" => "forge",
            "neoforge" => "neoforge",
            "liteloader" => "liteloader",
            _ => null,
        };
    }
}
