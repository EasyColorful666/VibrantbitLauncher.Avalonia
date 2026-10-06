using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using MinecraftLaunch;
using MinecraftLaunch.Base.Models.Game;
using MinecraftLaunch.Base.Models.Network;
using MinecraftLaunch.Components.Downloader;
using MinecraftLaunch.Components.Parser;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 自实现的原版安装器。
///
/// 不用 MinecraftLaunch 的 <c>VanillaInstaller</c>，卡点是它的流程写死成
/// 「下载 versions/&lt;id&gt;/&lt;id&gt;.json → 立刻 MinecraftParser.Parse()」，
/// 而 1.13 以前的版本（含 1.12.2）的版本 JSON 没有 arguments 字段，
/// Parse 会当场抛 InvalidOperationException，详见 <see cref="VersionJsonNormalizer"/>。
/// 库没有留出「JSON 落盘之后、解析之前」的钩子，所以这里按同样的步骤自己走一遍：
///
/// 下载版本 JSON → 规范化（补 arguments / 修时区）→ 落盘
/// → 交给库的 MinecraftParser 解析（第 1 步做完就不会再崩）
/// → 交给库的 MinecraftResourceDownloader 下载 client jar / 库 / 资源索引与资源对象。
///
/// 重活仍然全部在库里，这里只负责「下载 + 修补 + 解析」这三步。
/// </summary>
public static class VanillaDownloader
{
    private static readonly HttpClient _http = new();

    /// <summary>
    /// 安装（或补全）一个原版版本。
    /// </summary>
    /// <param name="mcFolder">.minecraft 根目录。</param>
    /// <param name="entry">版本清单条目。</param>
    /// <param name="onProgress">进度回调 (百分比, 步骤文案, 速度文案)。</param>
    public static async Task<MinecraftEntry> InstallAsync(
        string mcFolder,
        VersionManifestEntry entry,
        Action<int, string, string> onProgress = null)
    {
        if (string.IsNullOrWhiteSpace(mcFolder))
            throw new ArgumentException("未配置 .minecraft 目录", nameof(mcFolder));
        if (entry == null || string.IsNullOrWhiteSpace(entry.Id))
            throw new ArgumentException("版本信息为空", nameof(entry));

        onProgress?.Invoke(3, "下载版本信息", string.Empty);

        var url = DownloadManager.BmclApi.TryFindUrl(entry.Url);
        var json = await _http.GetStringAsync(url);

        // 1.13 以前没有 arguments 字段，库解析时会崩；这一步是这次修复的核心
        var (normalized, _) = VersionJsonNormalizer.Normalize(json);
        json = normalized;

        var versionDir = Path.Combine(mcFolder, "versions", entry.Id);
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, entry.Id + ".json"), json);

        onProgress?.Invoke(8, "解析版本", string.Empty);
        var minecraft = new MinecraftParser(mcFolder).GetMinecraft(entry.Id)
            ?? throw new InvalidOperationException($"版本解析失败：{entry.Id}");

        onProgress?.Invoke(10, "准备下载依赖", string.Empty);
        var downloader = new MinecraftResourceDownloader(minecraft) { AllowVerifyAssets = true };
        downloader.ProgressChanged += (_, args) =>
        {
            var ratio = args.TotalCount > 0
                ? (double)args.CompletedCount / args.TotalCount
                : args.Percentage / 100d;

            onProgress?.Invoke(
                10 + (int)(Math.Clamp(ratio, 0, 1) * 88),
                $"下载文件 {args.CompletedCount}/{args.TotalCount}",
                args.Speed > 0 ? $"{args.Speed / 1024 / 1024:F1} MB/s" : string.Empty);
        };

        // 并发数以设置页的「下载线程数」为准（1~64 之间夹紧，避免误填把网络或磁盘打满）
        var parallelism = Math.Clamp(SettingsService.Current.DownloadThreads, 1, 64);
        var result = await downloader.VerifyAndDownloadDependenciesAsync(parallelism);
        if (result.Failed.Any())
            throw new InvalidOperationException("部分依赖文件下载失败，请检查网络后重试");

        onProgress?.Invoke(100, "安装完成", string.Empty);
        return minecraft;
    }
}
