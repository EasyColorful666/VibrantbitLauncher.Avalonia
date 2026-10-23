using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Services;

/// <summary>
/// Java 运行时的查询与安装（数据源：Eclipse Adoptium / Temurin 的公开 REST API）。
///
/// <para>
/// <b>为什么用 Adoptium 而不是各家自己找链接</b>：它是 Eclipse 基金会托管、
/// 被大量启动器采用的 OpenJDK 发行版，API 稳定且直接给出「平台 × 架构 × 版本」
/// 的制品清单与 SHA256，不需要我们自己维护一堆硬编码 URL。
/// </para>
///
/// <para>
/// <b>国内可达性</b>：API 域名（api.adoptium.net）实测可用；但制品放在 GitHub Releases 上，
/// 国内经常很慢甚至下不动。所以每个版本都会额外给出清华 TUNA 的 Adoptium 镜像地址，
/// 两个候选交给 <see cref="ResilientFileDownloader"/> 轮换续传 —— 单个 JDK 包 180~200 MB，
/// 靠「断了就从头再来」几乎不可能下完。
/// </para>
/// </summary>
public static class JavaRuntimeService
{
    private const string ApiBase = "https://api.adoptium.net/v3";

    /// <summary>TUNA 的 Adoptium 镜像根。路径结构与官方发布页一致：/{major}/jdk/{arch}/{os}/{file}。</summary>
    private const string TunaRoot = "https://mirrors.tuna.tsinghua.edu.cn/Adoptium";

    /// <summary>API 不可用时的兜底主版本列表（都是 LTS，覆盖 99% 的整合包需求）。</summary>
    private static readonly int[] FallbackMajors = { 21, 17, 11, 8 };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VibrantbitLauncher/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    // ==================== 平台信息 ====================

    /// <summary>当前系统的 Adoptium os 取值：windows / linux / mac。</summary>
    public static string CurrentOs =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "mac"
        : "linux";

    /// <summary>当前进程架构的 Adoptium 取值：x64 / aarch64 / x86 / arm。</summary>
    public static string CurrentArch => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "aarch64",
        Architecture.X86 => "x86",
        Architecture.Arm => "arm",
        _ => "x64",
    };

    /// <summary>给界面看的平台描述。</summary>
    public static string PlatformText => $"{CurrentOs}-{CurrentArch}";

    /// <summary>
    /// 默认安装根目录：<c>%ProgramFiles%\Eclipse Adoptium</c> ——
    /// 也就是 **Temurin 官方安装器（MSI）自己使用的那个目录**。
    ///
    /// <para>
    /// 为什么跟官方安装器一致：Java 是机器级运行时，别的工具（HMCL / PCL / IDEA / 各种一键包）
    /// 找 Java 时认的就是这套约定路径。<如果这里自建一个 <c>runtime\jdk-21</c>，
    /// 启动器自己能用，但用户在别处既找不到、也无法复用。
    /// </para>
    ///
    /// <para>
    /// 目录结构同样对齐官方安装器：每个版本一个
    /// <c>jdk-&lt;版本&gt;-hotspot</c> 子目录（见 <see cref="BuildInstallFolderName"/>），
    /// 例如 <c>%ProgramFiles%\Eclipse Adoptium\jdk-21.0.12.1-hotspot\bin\java.exe</c>。
    /// </para>
    ///
    /// <para>
    /// <b>代价是写入需要管理员权限</b>：Program Files 默认只有 TrustedInstaller 与管理员的写权限。
    /// 所以安装前会先做一次可写探测（见 <see cref="EnsureWritable"/>），
    /// 没权限时直接给一句中文可操作提示，而不是让用户对着一句
    /// <c>Access to the path ... is denied</c> 发愣。
    /// </para>
    ///
    /// <para>
    /// 取不到 Program Files（极少数精简系统）时回落到启动器目录，保证功能不因此不可用。
    /// </para>
    /// </summary>
    public static string DefaultRuntimeRoot
    {
        get
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            if (string.IsNullOrWhiteSpace(programFiles))
            {
                // 精简系统 / 异常环境：SpecialFolder 可能返回空字符串
                Log.Warning("取不到 ProgramFiles，Java 默认目录回落到启动器目录");
                return Path.Combine(AppContext.BaseDirectory, "runtime");
            }

            return Path.Combine(programFiles, "Eclipse Adoptium");
        }
    }

    /// <summary>
    /// 某个版本在磁盘上的目录名，与 Temurin 官方安装器保持一致：
    /// <c>jdk-21.0.12.1-hotspot</c>。
    ///
    /// <para>
    /// 官方安装器用的是「去掉 build 号、补 <c>-hotspot</c> 后缀」的写法：
    /// 发行名 <c>jdk-21.0.12.1+1</c> → 目录 <c>jdk-21.0.12.1-hotspot</c>。
    /// 这里照做，用户在两处看到的才是同一个名字。
    /// </para>
    /// </summary>
    public static string BuildInstallFolderName(JavaReleaseItem item)
    {
        var name = item.ReleaseName;

        if (string.IsNullOrWhiteSpace(name))
            name = $"jdk-{item.Version}";

        // 去掉 build 元数据（+1 / +8）
        var plus = name.IndexOf('+');
        if (plus > 0)
            name = name[..plus];

        name = name.Trim();

        // 兜底：极少数条目给的是纯版本号
        if (!name.StartsWith("jdk-", StringComparison.OrdinalIgnoreCase))
            name = "jdk-" + name;

        if (!name.Contains("hotspot", StringComparison.OrdinalIgnoreCase))
            name += "-hotspot";

        return name;
    }

    /// <summary>
    /// 探测目录是否可写，不可写时抛出**可操作**的中文异常。
    ///
    /// <para>
    /// 为什么不在写入失败后再翻译：<see cref="Directory.CreateDirectory"/> 抛出的
    /// <see cref="UnauthorizedAccessException"/> 消息是全英文的
    /// <c>Access to the path 'C:\Program Files\...' is denied.</c>，
    /// 用户既不知道要「以管理员身份运行」，也不知道「其实可以换个目录」。
    /// 提前探测能给出这两条具体出路。
    /// </para>
    /// </summary>
    /// <param name="targetRoot">准备写入的根目录（不存在会被创建）。</param>
    public static void EnsureWritable(string targetRoot)
    {
        try
        {
            Directory.CreateDirectory(targetRoot);

            // 真正写一个探针文件：CreateDirectory 成功不代表能往里写
            // （目录可能已存在但 ACL 只给了读权限）
            var probe = Path.Combine(targetRoot, $".vbl-write-test-{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"没有权限写入 {targetRoot}。\n"
                + "该目录属于系统程序目录，需要管理员权限。请二选一：\n"
                + "· 关闭启动器，右键「以管理员身份运行」后重试；\n"
                + "· 或点「浏览…」换一个普通目录（例如 D:\\Java）。");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"无法在 {targetRoot} 创建文件：{ex.Message}\n"
                + "请检查磁盘空间，或点「浏览…」换一个目录。");
        }
    }

    // ==================== 版本查询 ====================

    /// <summary>
    /// 取可用的 Java 主版本列表（新 → 旧）。
    /// API 失败时返回内置 LTS 列表，保证界面永远有东西可选。
    /// </summary>
    public static async Task<IReadOnlyList<int>> GetAvailableMajorsAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync($"{ApiBase}/info/available_releases", ct)
                .ConfigureAwait(false);

            var info = JsonSerializer.Deserialize<AdoptiumAvailableReleases>(json);
            var majors = info?.AvailableLtsReleases is { Count: > 0 } lts
                ? lts
                : info?.AvailableReleases ?? new List<int>();

            var result = majors.Where(m => m > 0).Distinct().OrderByDescending(m => m).ToList();
            if (result.Count > 0)
            {
                Log.Information("Java 主版本列表（Adoptium）：{Majors}", string.Join(", ", result));
                return result;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "获取 Java 主版本列表失败，使用内置列表");
        }

        return FallbackMajors;
    }

    /// <summary>
    /// 取某个主版本在当前平台上的所有 GA 发行版（新 → 旧）。
    ///
    /// 端点：<c>/assets/feature_releases/{major}/ga</c>，返回的是**数组**，
    /// 每个元素是一个发行版，内部的 <c>binaries</c> 才是各平台制品 ——
    /// 这里按 os / arch / image_type=jdk 过滤出唯一一条。
    /// </summary>
    public static async Task<IReadOnlyList<JavaReleaseItem>> GetReleasesAsync(
        int major, CancellationToken ct = default, int pageSize = 20)
    {
        var os = CurrentOs;
        var arch = CurrentArch;

        var url = $"{ApiBase}/assets/feature_releases/{major}/ga"
                  + $"?architecture={arch}&image_type=jdk&os={os}&vendor=eclipse"
                  + $"&page=0&page_size={pageSize}&sort_order=DESC&sort_method=DEFAULT";

        var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize<List<AdoptiumRelease>>(json) ?? new List<AdoptiumRelease>();

        var items = new List<JavaReleaseItem>(releases.Count);

        foreach (var release in releases)
        {
            var binary = release.Binaries.FirstOrDefault(b =>
                string.Equals(b.Os, os, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(b.Architecture, arch, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(b.ImageType, "jdk", StringComparison.OrdinalIgnoreCase));

            var package = binary?.Package;
            if (package?.Link is null || string.IsNullOrWhiteSpace(package.Name))
                continue;

            var version = release.VersionData?.Semver
                          ?? release.VersionData?.OpenJdkVersion
                          ?? release.ReleaseName
                          ?? $"{major}";

            items.Add(new JavaReleaseItem
            {
                Major = release.VersionData?.Major > 0 ? release.VersionData!.Major : major,
                Version = version,
                ReleaseName = release.ReleaseName ?? package.Name!,
                IsLts = string.Equals(release.VersionData?.Optional, "LTS", StringComparison.OrdinalIgnoreCase),
                FileName = package.Name!,
                DownloadUrl = package.Link!,
                MirrorUrl = BuildMirrorUrl(major, arch, os, package.Name!),
                Size = package.Size,
                Sha256 = string.IsNullOrWhiteSpace(package.Checksum) ? null : package.Checksum!.Trim().ToLowerInvariant(),
                ReleasedAt = release.Timestamp,
                Platform = $"{os}-{arch}",
            });
        }

        Log.Information("Java {Major} 可用发行版：{Count} 个（{Platform}）", major, items.Count, $"{os}-{arch}");
        return items;
    }

    /// <summary>
    /// 拼国内镜像地址。镜像目录结构与官方发布页一致：
    /// <c>{TunaRoot}/{major}/jdk/{arch}/{os}/{fileName}</c>。
    ///
    /// 文件名里的 <c>jdk_x64_windows_hotspot</c> 这类标识与官方完全同名，
    /// 所以不需要做任何映射；镜像上没有的版本会在下载时自动回落到官方地址。
    /// </summary>
    private static string? BuildMirrorUrl(int major, string arch, string os, string fileName)
        => $"{TunaRoot}/{major}/jdk/{arch}/{os}/{fileName}";

    // ==================== 安装 ====================

    /// <summary>
    /// 下载并解压一个 JDK 到 <paramref name="targetDir"/>（解压后 <c>bin/java</c> 直接位于该目录下）。
    /// </summary>
    /// <param name="item">要安装的版本。</param>
    /// <param name="targetDir">目标目录，例如 <c>runtime/jdk-21</c>。</param>
    /// <param name="onProgress">进度回调（0-100，阶段文案）。可能从后台线程调用。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>java 可执行文件全路径。</returns>
    public static async Task<string> InstallAsync(
        JavaReleaseItem item, string targetDir, Action<int, string> onProgress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var javaExeName = OperatingSystem.IsWindows() ? "java.exe" : "java";

        // 已经装好了就直接返回（重装同一版本不必再下 200 MB）
        var existing = FindJavaExe(targetDir);
        if (existing is not null)
        {
            onProgress(100, $"该版本已存在于 {targetDir}");
            return existing;
        }

        // 先探测可写再开始下载：默认目录在 %ProgramFiles% 下，没管理员权限时
        // 这里会立刻给出「以管理员身份运行 / 换个目录」的提示，
        // 而不是让用户白等 200 MB 下完才在解压阶段失败。
        onProgress(1, "检查安装目录权限");
        EnsureWritable(targetDir);

        // 压缩包落在系统临时目录：200 MB 不该堆在 .minecraft 或启动器目录里
        var zipPath = Path.Combine(Path.GetTempPath(), $"vbl-jdk-{item.Major}-{Guid.NewGuid():N}.zip");

        try
        {
            onProgress(1, $"准备下载 JDK {item.Version}（{item.SizeText}）");

            var urls = new[] { item.DownloadUrl, item.MirrorUrl }
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var ok = await ResilientFileDownloader.DownloadAsync(
                urls,
                zipPath,
                (have, total) =>
                {
                    var percent = total > 0 ? (int)Math.Clamp(have * 100 / total, 0, 100) : 0;
                    var sizeText = total > 0
                        ? $"{have / 1024d / 1024d:0.0} / {total / 1024d / 1024d:0.0} MB"
                        : $"{have / 1024d / 1024d:0.0} MB";
                    onProgress(Math.Clamp(percent, 0, 82), $"下载 JDK {item.Version}　{sizeText}");
                },
                ct).ConfigureAwait(false);

            if (!ok)
                throw new InvalidOperationException("JDK 压缩包下载不完整，请重试或换个网络环境");

            // 体积校验：清单给了确切大小就严格比对（截断的包解压到一半会报 CRC 错，不如提前说清楚）
            var actualSize = new FileInfo(zipPath).Length;
            if (item.Size > 0 && actualSize != item.Size)
                throw new InvalidOperationException(
                    $"JDK 压缩包大小不符（期望 {item.Size} 字节，实际 {actualSize} 字节）");

            if (!string.IsNullOrEmpty(item.Sha256))
            {
                onProgress(84, "校验 SHA256");
                var actual = await ComputeSha256Async(zipPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("JDK 压缩包校验失败（SHA256 不匹配），请重试");
            }

            onProgress(88, "解压 JDK");
            ExtractWithRootStrip(zipPath, targetDir, percent =>
                onProgress(88 + (int)(percent * 0.11), $"解压 JDK　{percent}%"));

            var javaPath = FindJavaExe(targetDir)
                ?? throw new InvalidOperationException(
                    $"解压完成但找不到 {javaExeName}，目录结构可能不符合预期：{targetDir}");

            onProgress(100, $"JDK {item.Version} 安装完成");
            Log.Information("JDK 安装完成：{Version} → {Path}", item.Version, javaPath);
            return javaPath;
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    /// <summary>
    /// 解压 JDK 包，并**剥掉顶层目录**。
    ///
    /// Adoptium 的 zip 里所有文件都在一个 <c>jdk-21.0.12.1+1/</c> 这样的根目录下。
    /// 直接解压会得到 <c>runtime/jdk-21/jdk-21.0.12.1+1/bin/java.exe</c> —— 路径里多一层随机版本号，
    /// 用户和我们自己都不好引用。这里把公共前缀去掉，让 <c>bin/java.exe</c> 直接落在目标目录下。
    /// </summary>
    private static void ExtractWithRootStrip(string zipPath, string targetDir, Action<int> onPercent)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        if (entries.Count == 0)
            throw new InvalidOperationException("JDK 压缩包是空的");

        var prefix = CommonRootPrefix(entries.Select(e => e.FullName));
        var total = entries.Count;
        var done = 0;

        foreach (var entry in entries)
        {
            var relative = prefix is null ? entry.FullName : entry.FullName[prefix.Length..];
            if (string.IsNullOrWhiteSpace(relative))
                continue;

            var target = Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar));

            // 防目录穿越：zip 条目里若有 ..，解压后可能跑到目标目录之外
            var full = Path.GetFullPath(target);
            var rootFull = Path.GetFullPath(targetDir);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                continue;

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            entry.ExtractToFile(full, overwrite: true);

            done++;
            // 每 20 个条目报一次就够，几千个条目逐条上报只会拖慢解压
            if (done % 20 == 0 || done == total)
                onPercent((int)(done * 100d / total));
        }
    }

    /// <summary>算所有条目路径的公共顶层目录（带结尾的 /）。没有公共前缀时返回 null。</summary>
    private static string? CommonRootPrefix(IEnumerable<string> fullNames)
    {
        string? prefix = null;

        foreach (var name in fullNames)
        {
            var slash = name.IndexOf('/');
            if (slash <= 0)
                return null;   // 有文件直接位于根部 —— 不存在统一前缀

            var head = name[..(slash + 1)];
            if (prefix is null)
                prefix = head;
            else if (!string.Equals(prefix, head, StringComparison.Ordinal))
                return null;
        }

        return prefix;
    }

    /// <summary>在目录下找 java 可执行文件（bin/java[.exe]）。</summary>
    public static string? FindJavaExe(string home)
    {
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
            return null;

        var candidate = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        return File.Exists(candidate) ? candidate : null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ==================== 已安装扫描 ====================

    /// <summary>
    /// 扫描已安装的 Java。
    ///
    /// <para>
    /// 除了页面上选定的根目录，还会扫一遍各发行版在 Windows 上的**约定安装位置** ——
    /// 因为用户很可能早就用官方安装器装过 JDK，不该逼他再去设置里手动指路径。
    /// 这也是「安装到官方安装器路径」的另一个好处：装完就能被这里认出来。
    /// </para>
    /// </summary>
    public static IReadOnlyList<JavaInstallation> ScanInstalled(string runtimeRoot)
    {
        var result = new List<JavaInstallation>();

        void Add(string? home)
        {
            if (string.IsNullOrWhiteSpace(home))
                return;

            var java = FindJavaExe(home);
            if (java is null)
                return;

            if (result.Any(r => string.Equals(r.JavaPath, java, StringComparison.OrdinalIgnoreCase)))
                return;

            result.Add(new JavaInstallation
            {
                Home = home,
                JavaPath = java,
                Version = ReadVersionQuick(home),
                IsSelected = string.Equals(
                    SettingsService.Current.JavaPath, java, StringComparison.OrdinalIgnoreCase),
            });
        }

        // 把某个根下的直接子目录都当成候选 JDK 目录
        void ScanRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return;

            try
            {
                foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d))
                    Add(dir);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "扫描 Java 目录失败：{Root}", root);
            }
        }

        // 1) 页面选定的根目录（默认 = Eclipse Adoptium）
        ScanRoot(runtimeRoot);

        // 2) 各发行版的约定目录（即使用户换了别的根目录，这些也该被认出来）
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            ScanRoot(Path.Combine(programFiles, "Eclipse Adoptium"));
            ScanRoot(Path.Combine(programFiles, "AdoptOpenJDK"));
            ScanRoot(Path.Combine(programFiles, "Java"));          // Oracle JDK
            ScanRoot(Path.Combine(programFiles, "Zulu"));          // Azul
            ScanRoot(Path.Combine(programFiles, "Amazon Corretto"));
            ScanRoot(Path.Combine(programFiles, "Semeru"));        // IBM
            ScanRoot(Path.Combine(programFiles, "Microsoft"));     // Microsoft Build of OpenJDK（jdk-17.x 子目录）
        }

        // 3) 用户级安装（MSI 选 per-user 时落在 LOCALAPPDATA\Programs）
        var localApps = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApps))
            ScanRoot(Path.Combine(localApps, "Programs", "Eclipse Adoptium"));

        // 4) 用户手动指定的（可能在任何地方）
        var configured = SettingsService.Current.JavaPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // 用户可能填的是 java.exe 本身，也可能是 JAVA_HOME 目录
            var home = Directory.Exists(configured)
                ? configured
                : Path.GetDirectoryName(Path.GetDirectoryName(configured));

            Add(home);
        }

        return result;
    }

    /// <summary>
    /// 读版本号：优先解析 JDK 自带的 <c>release</c> 文件（<c>JAVA_VERSION="21.0.12"</c>），
    /// 比启动一次 java 进程快得多，也不受被杀软拦截的影响。
    /// </summary>
    private static string ReadVersionQuick(string home)
    {
        try
        {
            var release = Path.Combine(home, "release");
            if (File.Exists(release))
            {
                foreach (var line in File.ReadLines(release))
                {
                    if (!line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                        continue;

                    return line["JAVA_VERSION=".Length..].Trim().Trim('"');
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取 Java 版本失败：{Home}", home);
        }

        return Path.GetFileName(home.TrimEnd(Path.DirectorySeparatorChar));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时文件删不掉不影响安装结果，交给系统清理
        }
    }
}
