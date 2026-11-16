using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VibrantbitLauncher.Services;

/// <summary>发布里的一个安装包（按平台分文件，界面只挑当前系统那一个）。</summary>
public sealed class ReleaseAsset
{
    public string Name { get; init; } = string.Empty;

    /// <summary>官方 GitHub 直链。<c>gh-proxy</c> 之类的加速地址都是在它前面拼前缀得来的。</summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>字节数；接口没给时为 0。</summary>
    public long Size { get; init; }

    /// <summary>GitHub 给出的 sha256（小写十六进制）；没给时为空串。</summary>
    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>一次发布的信息，只取界面要用到的字段。</summary>
public sealed class ReleaseInfo
{
    public string TagName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public bool Prerelease { get; init; }
    public DateTimeOffset PublishedAt { get; init; }

    /// <summary>这次发布挂出来的全部文件（含 SHA256SUMS.txt）。</summary>
    public List<ReleaseAsset> Assets { get; init; } = new();
}

/// <summary>
/// 更新包的三条下载线路。
///
/// <para>
/// GitHub 的 release 直链在国内经常是「能连上、只有几十 KB/s」，所以要给加速通道；
/// 但加速站本身也可能挂，所以三条都要留，让用户自己选一条能用的。
/// </para>
/// </summary>
public enum UpdateSourceChannel
{
    /// <summary>IPv4 加速（<c>v4.gh-proxy.com</c>），最常用。</summary>
    ProxyV4,

    /// <summary>IPv4 / IPv6 加速（<c>v6.gh-proxy.com</c>），走 IPv6 时通常更快。</summary>
    ProxyV6,

    /// <summary>直连 GitHub，不经任何第三方。</summary>
    Direct
}

/// <summary>检查更新的结果。用明确的几种状态，界面据此决定弹什么。</summary>
public enum UpdateCheckStatus
{
    /// <summary>检查成功，且已有更新的版本。</summary>
    UpdateAvailable,
    /// <summary>检查成功，当前已是最新。</summary>
    UpToDate,
    /// <summary>网络不通 / 接口返回异常。</summary>
    Failed
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message, ReleaseInfo? Latest = null);

/// <summary>
/// 检查更新。
///
/// <para>
/// 走 GitHub 的公开 Releases 接口，不需要任何密钥，也不下载安装包 ——
/// 只负责回答「有没有更新的版本」并把发布页链接交给界面。
/// 这样用户点「查看」是打开浏览器，安装方式与手动下载完全一致，
/// 不会出现「自动更新把程序自己换掉了」这类难以排查的问题。
/// </para>
///
/// <para>
/// <b>为什么不静默自动下载</b>：本工程的发布包按平台分多个文件，
/// 选错架构会把能跑的程序换成一个起不来的 exe。这件事必须由人看一眼再决定。
/// </para>
/// </summary>
public static class UpdateService
{
    private const string Owner = "EasyColorful666";
    private const string Repo = "VibrantbitLauncher.Avalonia";

    /// <summary>GitHub 要求带 UA，否则直接 403。</summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static UpdateService()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VibrantbitLauncher");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
    }

    /// <summary>当前程序版本（四段式），解析不出来时按 0.0.0.0 处理。</summary>
    public static Version CurrentVersion =>
        Version.TryParse(App.GetDisplayVersion(), out var version) ? version : new Version(0, 0, 0, 0);

    /// <summary>发布页地址，界面上的「查看更新日志」直接用这个。</summary>
    public static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>
    /// 仓库根地址。AGPL 第 13 条要求向使用者提供对应源码的获取方式，
    /// 界面上的「获取源代码」就指向这里 —— 改了域名只需要动这一个地方。
    /// </summary>
    public static string RepositoryUrl => $"https://github.com/{Owner}/{Repo}";

    /// <summary>
    /// 检查更新。
    /// </summary>
    /// <param name="includePrerelease">
    /// 是否包含测试版。开启后按发布时间倒序取最近若干条，含预发布；
    /// 关闭时只问 <c>/releases/latest</c>（GitHub 保证它不含预发布）。
    /// </param>
    public static async Task<UpdateCheckResult> CheckAsync(
        bool includePrerelease,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var release = includePrerelease
                ? await GetNewestIncludingPrereleaseAsync(cancellationToken).ConfigureAwait(false)
                : await GetLatestStableAsync(cancellationToken).ConfigureAwait(false);

            if (release is null)
                return new UpdateCheckResult(UpdateCheckStatus.Failed, "没有取到任何发布记录");

            if (!TryParseTag(release.TagName, out var remote))
            {
                Log.Warning("检查更新：无法解析远端版本号 {Tag}", release.TagName);
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    $"远端版本号无法识别（{release.TagName}）",
                    release);
            }

            var current = CurrentVersion;
            if (remote <= current)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpToDate,
                    $"当前已是最新版本（{current}）",
                    release);
            }

            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                $"发现新版本 {remote}（当前 {current}）",
                release);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, "检查已取消");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "检查更新失败");
            return new UpdateCheckResult(UpdateCheckStatus.Failed, "网络不通，暂时无法检查更新");
        }
    }

    private static async Task<ReleaseInfo?> GetLatestStableAsync(CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        var release = await Http.GetFromJsonAsync<GitHubRelease>(url, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return release is null ? null : ToInfo(release);
    }

    private static async Task<ReleaseInfo?> GetNewestIncludingPrereleaseAsync(CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=20";
        var releases = await Http.GetFromJsonAsync<List<GitHubRelease>>(url, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (releases is null || releases.Count == 0)
            return null;

        // 接口本身按创建时间倒序；这里再按「解析出的版本号」取最大，
        // 避免「先发测试版、后补正式版」时把旧的正式版当成最新。
        ReleaseInfo? best = null;
        Version? bestVersion = null;

        foreach (var release in releases)
        {
            if (release.Draft)
                continue;

            var info = ToInfo(release);
            if (!TryParseTag(info.TagName, out var version))
                continue;

            if (bestVersion is null || version > bestVersion)
            {
                bestVersion = version;
                best = info;
            }
        }

        return best;
    }

    private static ReleaseInfo ToInfo(GitHubRelease release) => new()
    {
        TagName = release.TagName ?? string.Empty,
        Name = release.Name ?? release.TagName ?? string.Empty,
        HtmlUrl = release.HtmlUrl ?? ReleasesPageUrl,
        Body = release.Body ?? string.Empty,
        Prerelease = release.Prerelease,
        PublishedAt = release.PublishedAt,
        Assets = (release.Assets ?? new List<GitHubAsset>())
            .Where(a => !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
            .Select(a => new ReleaseAsset
            {
                Name = a.Name ?? string.Empty,
                DownloadUrl = a.BrowserDownloadUrl!,
                Size = a.Size,
                // GitHub 的 digest 形如 sha256:9f690e35…；老接口没有这个字段
                Sha256 = ParseSha256Digest(a.Digest)
            })
            .ToList()
    };

    /// <summary>把 <c>sha256:xxxx</c> 拆成小写十六进制；格式不对就返回空串（当作「没有哈希」）。</summary>
    private static string ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return string.Empty;

        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var hex = digest[prefix.Length..].Trim();
        return hex.Length == 64 ? hex.ToLowerInvariant() : string.Empty;
    }

    // ==================== 安装包选择 / 下载线路 ====================

    /// <summary>IPv4 加速前缀（用法：前缀 + 原始 GitHub 链接）。</summary>
    public const string ProxyV4Prefix = "https://v4.gh-proxy.com/";

    /// <summary>IPv4 / IPv6 加速前缀。</summary>
    public const string ProxyV6Prefix = "https://v6.gh-proxy.com/";

    /// <summary>
    /// 当前系统对应的安装包。
    ///
    /// <para>
    /// 发布脚本按 <c>VibrantbitLauncher.Avalonia-&lt;os&gt;-&lt;arch&gt;.&lt;ext&gt;</c> 命名，
    /// 这里按同样的规则反查：先要「系统 + 架构」完全一致的，没有就退到只看系统，
    /// 都没有返回 <c>null</c>（界面会说明「这次发布没有你这个平台的包」）。
    /// 绝不能随手挑一个 —— 下错架构的包装上去就是「更新完打不开」。
    /// </para>
    /// </summary>
    public static ReleaseAsset? PickAssetForCurrentPlatform(ReleaseInfo release)
    {
        if (release is null || release.Assets.Count == 0)
            return null;

        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux";

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => string.Empty
        };

        // 前缀形如 VibrantbitLauncher.Avalonia-win-x64.
        var exact = $"-{os}-{arch}.";
        var byOs = $"-{os}-";

        var match = release.Assets.FirstOrDefault(a =>
            a.Name.Contains(exact, StringComparison.OrdinalIgnoreCase));

        match ??= arch.Length == 0
            ? null
            : release.Assets.FirstOrDefault(a =>
                a.Name.Contains(byOs, StringComparison.OrdinalIgnoreCase));

        return match;
    }

    /// <summary>
    /// 按线路拼出下载地址。
    ///
    /// <para>
    /// gh-proxy 的用法就是「前缀 + 完整原始链接」，因此这里不做任何转义。
    /// 直连时原样返回。
    /// </para>
    /// </summary>
    public static string BuildDownloadUrl(ReleaseAsset asset, UpdateSourceChannel channel)
    {
        var url = asset?.DownloadUrl ?? string.Empty;
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        return channel switch
        {
            UpdateSourceChannel.ProxyV4 => ProxyV4Prefix + url,
            UpdateSourceChannel.ProxyV6 => ProxyV6Prefix + url,
            _ => url
        };
    }

    /// <summary>
    /// 一次下载要试的全部候选地址：选中的线路排第一，其余两条兜底。
    ///
    /// <para>
    /// 顺序为「用户选的 → 另一条加速 → 直连」。加速站挂掉时自动落到直连，
    /// 用户不必回到窗口里再点一次。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> BuildCandidates(ReleaseAsset asset, UpdateSourceChannel preferred)
    {
        var order = preferred switch
        {
            UpdateSourceChannel.ProxyV4 => new[]
                { UpdateSourceChannel.ProxyV4, UpdateSourceChannel.ProxyV6, UpdateSourceChannel.Direct },
            UpdateSourceChannel.ProxyV6 => new[]
                { UpdateSourceChannel.ProxyV6, UpdateSourceChannel.ProxyV4, UpdateSourceChannel.Direct },
            _ => new[]
                { UpdateSourceChannel.Direct, UpdateSourceChannel.ProxyV4, UpdateSourceChannel.ProxyV6 }
        };

        var list = new List<string>(3);
        foreach (var channel in order)
        {
            var url = BuildDownloadUrl(asset, channel);
            if (!string.IsNullOrEmpty(url)
                && !list.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(url);
            }
        }

        return list;
    }

    /// <summary>
    /// 更新包落盘目录。
    ///
    /// 优先放程序目录下的 <c>updates/</c>（便携版随时能找到），
    /// 目录不可写（装在 Program Files 之类）时退到临时目录。
    /// </summary>
    public static string ResolveDownloadFolder()
    {
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "updates");
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "程序目录下无法创建 updates，改用临时目录");
            var fallback = Path.Combine(Path.GetTempPath(), "VibrantbitLauncher-updates");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    /// <summary>
    /// 校验下载回来的安装包。
    ///
    /// <para>
    /// 这个包要经过第三方加速站转发，属于「内容可被链路替换」的输入，
    /// 必须对着 GitHub 接口给的 sha256 核一遍。期望值为空时（老发布没带 digest）
    /// 只能跳过校验，但会在日志里留一条。
    /// </para>
    /// </summary>
    /// <returns>校验通过返回 <c>null</c>；不通过返回给用户看的原因。</returns>
    public static async Task<string?> VerifySha256Async(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            Log.Warning("更新包没有可用的 sha256，跳过校验：{File}", path);
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var hash = await System.Security.Cryptography.SHA256
                .HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);

            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            if (string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                return null;

            Log.Warning("更新包校验不通过：期望 {Expected}，实际 {Actual}", expectedSha256, actual);
            return "安装包校验不通过，可能在下载链路中被替换，已删除";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "更新包校验失败：{File}", path);
            return "无法校验安装包（" + ex.Message + "）";
        }
    }

    /// <summary>把 <c>v1.0.12.0</c> 这类 tag 解析成版本号。</summary>
    private static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var text = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out version!);
    }

    /// <summary>GitHub 发布接口的原始结构（只声明用到的字段）。</summary>
    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset PublishedAt { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; init; }
    }

    /// <summary>发布里的一个文件。</summary>
    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }

        /// <summary>形如 <c>sha256:…</c>（GitHub 2025 年起提供）。</summary>
        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}
