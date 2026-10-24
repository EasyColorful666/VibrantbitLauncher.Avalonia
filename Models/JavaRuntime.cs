using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VibrantbitLauncher.Models;

/// <summary>
/// Adoptium（Eclipse Temurin）REST API 的反序列化模型。
///
/// <para>
/// 接口是公开的，不需要 key：<c>https://api.adoptium.net/v3/…</c>。
/// 这里只映射用得到的字段，其余一律忽略 —— 上游随时可能加字段，
/// 多映射反而会让「字段改名」变成反序列化异常。
/// </para>
/// </summary>
public sealed class AdoptiumAvailableReleases
{
    [JsonPropertyName("available_releases")]
    public List<int> AvailableReleases { get; set; } = new();

    [JsonPropertyName("available_lts_releases")]
    public List<int> AvailableLtsReleases { get; set; } = new();

    [JsonPropertyName("most_recent_lts")]
    public int MostRecentLts { get; set; }

    [JsonPropertyName("most_recent_feature_release")]
    public int MostRecentFeatureRelease { get; set; }
}

/// <summary>一个具体的发行版（如 jdk-21.0.12.1+1）。</summary>
public sealed class AdoptiumRelease
{
    [JsonPropertyName("release_name")]
    public string? ReleaseName { get; set; }

    [JsonPropertyName("release_type")]
    public string? ReleaseType { get; set; }

    [JsonPropertyName("version_data")]
    public AdoptiumVersionData? VersionData { get; set; }

    [JsonPropertyName("binaries")]
    public List<AdoptiumBinary> Binaries { get; set; } = new();

    /// <summary>发布时间（ISO 8601）。</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; set; }
}

/// <summary>版本号细节。</summary>
public sealed class AdoptiumVersionData
{
    [JsonPropertyName("semver")]
    public string? Semver { get; set; }

    [JsonPropertyName("major")]
    public int Major { get; set; }

    [JsonPropertyName("minor")]
    public int Minor { get; set; }

    [JsonPropertyName("security")]
    public int Security { get; set; }

    [JsonPropertyName("patch")]
    public int Patch { get; set; }

    [JsonPropertyName("build")]
    public int Build { get; set; }

    [JsonPropertyName("openjdk_version")]
    public string? OpenJdkVersion { get; set; }

    /// <summary>LTS / 空。</summary>
    [JsonPropertyName("optional")]
    public string? Optional { get; set; }
}

/// <summary>某个平台上的制品（同一发行版会有 windows/linux/mac 多份）。</summary>
public sealed class AdoptiumBinary
{
    [JsonPropertyName("os")]
    public string? Os { get; set; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; set; }

    [JsonPropertyName("image_type")]
    public string? ImageType { get; set; }

    [JsonPropertyName("jvm_impl")]
    public string? JvmImpl { get; set; }

    /// <summary>压缩包（zip / tar.gz）。</summary>
    [JsonPropertyName("package")]
    public AdoptiumPackage? Package { get; set; }

    /// <summary>
    /// 平台安装包。**Windows 的 MSI 就放在这里**，与 <see cref="Package"/> 并列，
    /// 而不是另起一个 binary —— 早先按 package 名后缀找 .msi 是找不到的。
    /// 其他平台（或上游没提供安装包时）为 null。
    /// </summary>
    [JsonPropertyName("installer")]
    public AdoptiumPackage? Installer { get; set; }
}

/// <summary>可下载的压缩包。</summary>
public sealed class AdoptiumPackage
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("checksum")]
    public string? Checksum { get; set; }
}

/// <summary>
/// 界面上展示的一条 JDK 版本（已按当前平台过滤好，直接绑定用）。
/// </summary>
public sealed class JavaReleaseItem
{
    /// <summary>主版本号，如 21。</summary>
    public int Major { get; init; }

    /// <summary>展示用版本字符串，如 <c>21.0.12+101.0.LTS</c>。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>发行名，如 <c>jdk-21.0.12.1+1</c>。</summary>
    public string ReleaseName { get; init; } = string.Empty;

    /// <summary>是否 LTS 版本。</summary>
    public bool IsLts { get; init; }

    /// <summary>压缩包文件名。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>官方下载地址（GitHub Releases）。</summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>备用镜像地址（国内镜像），可能为空。</summary>
    public string? MirrorUrl { get; init; }

    /// <summary>压缩包字节数；0 表示上游没给。</summary>
    public long Size { get; init; }

    /// <summary>SHA256（小写十六进制）。</summary>
    public string? Sha256 { get; init; }

    // ===== 安装包（Windows 专用） =====
    //
    // Temurin 在 Windows 上同时发布 .zip 与 .msi；MSI 是官方安装器（WiX 打包），
    // 可以 msiexec /qn 静默安装到指定目录，省掉「解压 + 手工摆目录」这一步。
    // 只有 Windows 的制品才有，其他平台这些字段恒为空。

    /// <summary>MSI 安装包文件名，如 <c>OpenJDK21U-jdk_x64_windows_hotspot_21.0.5_11.msi</c>。</summary>
    public string? InstallerFileName { get; init; }

    /// <summary>MSI 官方下载地址。</summary>
    public string? InstallerUrl { get; init; }

    /// <summary>MSI 的国内镜像地址，可能为空。</summary>
    public string? InstallerMirrorUrl { get; init; }

    /// <summary>MSI 字节数；0 表示上游没给。</summary>
    public long InstallerSize { get; init; }

    /// <summary>MSI 的 SHA256（小写十六进制）。</summary>
    public string? InstallerSha256 { get; init; }

    /// <summary>是否有可用的官方安装包（Windows 才有）。</summary>
    public bool HasInstaller => !string.IsNullOrWhiteSpace(InstallerUrl);

    /// <summary>发布时间。</summary>
    public DateTimeOffset? ReleasedAt { get; init; }

    /// <summary>平台描述，如 <c>windows-x64</c>。</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>体积文案（MB）。</summary>
    public string SizeText => Size > 0 ? $"{Size / 1024d / 1024d:0} MB" : "未知体积";

    /// <summary>发布日晚期文案。</summary>
    public string DateText => ReleasedAt is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd") : string.Empty;

    /// <summary>列表右侧的一行摘要：LTS 标记 + 平台 + 体积 + 日期。</summary>
    public string SummaryText
    {
        get
        {
            var parts = new List<string>(5);
            if (IsLts)
                parts.Add("LTS");
            if (!string.IsNullOrEmpty(Platform))
                parts.Add(Platform);
            parts.Add(SizeText);
            if (HasInstaller)
                parts.Add("MSI 安装包");
            if (!string.IsNullOrEmpty(DateText))
                parts.Add(DateText);

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>本机已安装的一个 Java 运行时。</summary>
public sealed class JavaInstallation
{
    /// <summary>运行时根目录（含 bin/java）。</summary>
    public string Home { get; init; } = string.Empty;

    /// <summary>java 可执行文件全路径。</summary>
    public string JavaPath { get; init; } = string.Empty;

    /// <summary>版本描述，读不到时用目录名。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>是否就是设置里当前选中的那个。</summary>
    public bool IsSelected { get; set; }

    /// <summary>展示文案：版本 + 路径。</summary>
    public string DisplayText => string.IsNullOrEmpty(Version) ? Home : $"{Version}　{Home}";
}
