using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VibrantbitLauncher.Services;

/// <summary>一次发布的信息，只取界面要用到的字段。</summary>
public sealed class ReleaseInfo
{
    public string TagName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public bool Prerelease { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
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
        PublishedAt = release.PublishedAt
    };

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
    }
}
