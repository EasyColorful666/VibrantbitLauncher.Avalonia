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
    /// 实际使用的 Java 安装根目录：用户选过就用用户选的，否则用默认值。
    ///
    /// <para>
    /// 这个值必须持久化（<see cref="AppSettings.JavaRuntimeRoot"/>）：
    /// 否则用户把 JDK 装到 D:\Java 后，下载页还认得（它读输入框），
    /// 设置页 / 启动页却认不出来，表现就是「装完检测不到」。
    /// </para>
    /// </summary>
    public static string ResolveRuntimeRoot()
    {
        var configured = SettingsService.Current.JavaRuntimeRoot;
        return string.IsNullOrWhiteSpace(configured) ? DefaultRuntimeRoot : configured;
    }

    /// <summary>某个版本在磁盘上的目录名，与 Temurin 官方安装器保持一致：
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
            // 同一发行版在 Windows 上会给出两条制品：package（zip）与 installer（MSI），
            // 二者在同一个 binary 里并列。所以这里只按 os / arch / image_type 选 binary，
            // 再分别从 package 与 installer 取压缩包和安装器。
            var binaries = release.Binaries
                .Where(b => string.Equals(b.Os, os, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(b.Architecture, arch, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(b.ImageType, "jdk", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (binaries.Count == 0)
                continue;

            var zipBinary = binaries.FirstOrDefault(b =>
                b.Package?.Link is not null && !string.IsNullOrWhiteSpace(b.Package.Name));

            var installerBinary = binaries.FirstOrDefault(b =>
                b.Installer?.Link is not null && !string.IsNullOrWhiteSpace(b.Installer.Name));

            // zip 是跨平台通用路径；Windows 上即使只有 MSI 也能装
            if (zipBinary?.Package?.Link is null && installerBinary?.Installer?.Link is null)
                continue;

            var version = release.VersionData?.Semver
                          ?? release.VersionData?.OpenJdkVersion
                          ?? release.ReleaseName
                          ?? $"{major}";

            var mainPackage = zipBinary?.Package ?? installerBinary!.Installer!;

            items.Add(new JavaReleaseItem
            {
                Major = release.VersionData?.Major > 0 ? release.VersionData!.Major : major,
                Version = version,
                ReleaseName = release.ReleaseName ?? mainPackage.Name!,
                IsLts = string.Equals(release.VersionData?.Optional, "LTS", StringComparison.OrdinalIgnoreCase),

                FileName = zipBinary?.Package?.Name ?? string.Empty,
                DownloadUrl = zipBinary?.Package?.Link ?? string.Empty,
                MirrorUrl = zipBinary?.Package?.Name is { Length: > 0 } zipName
                    ? BuildMirrorUrl(major, arch, os, zipName)
                    : null,
                Size = zipBinary?.Package?.Size ?? 0,
                Sha256 = string.IsNullOrWhiteSpace(zipBinary?.Package?.Checksum)
                    ? null
                    : zipBinary!.Package!.Checksum!.Trim().ToLowerInvariant(),

                InstallerFileName = installerBinary?.Installer?.Name,
                InstallerUrl = installerBinary?.Installer?.Link,
                InstallerMirrorUrl = installerBinary?.Installer?.Name is { Length: > 0 } msiName
                    ? BuildMirrorUrl(major, arch, os, msiName)
                    : null,
                InstallerSize = installerBinary?.Installer?.Size ?? 0,
                InstallerSha256 = string.IsNullOrWhiteSpace(installerBinary?.Installer?.Checksum)
                    ? null
                    : installerBinary!.Installer!.Checksum!.Trim().ToLowerInvariant(),

                ReleasedAt = release.Timestamp,
                Platform = $"{os}-{arch}",
            });
        }

        Log.Information("Java {Major} 可用发行版：{Count} 个（{Platform}，含 MSI {WithMsi} 个）",
            major, items.Count, $"{os}-{arch}", items.Count(i => i.HasInstaller));
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
    /// 安装 MSI 时勾选的组件。
    ///
    /// <para>
    /// 只装主程序（<c>FeatureMain</c>），**不动系统 PATH / JAVA_HOME**：
    /// </para>
    /// <list type="bullet">
    /// <item>启动器自己会给每次启动指定 <c>java.exe</c> 全路径，不依赖 PATH；</item>
    /// <item>官方安装器顺带写入的 <c>FeatureEnvironment</c> / <c>FeatureJavaHome</c> 是机器级的，
    /// 装第二个 JDK 就会把前一个覆盖掉，用户装的 Java 8 和 Java 21 会互相打架；</item>
    /// <item>低端机用户往往分不清「PATH 里是哪个 Java」，一旦被改坏很难自己修回来。</item>
    /// </list>
    ///
    /// <para>
    /// 需要「装完能在 cmd 里直接 java -version」时，把这里改成
    /// <c>FeatureMain,FeatureEnvironment,FeatureJavaHome</c> 即可（安装后会提示需要重启终端）。
    /// </para>
    /// </summary>
    private const string MsiFeatures = "FeatureMain";

    /// <summary>
    /// 下载并安装一个 JDK 到 <paramref name="targetDir"/>（装完后 <c>bin/java</c> 直接位于该目录下）。
    ///
    /// <para>
    /// 两条路径：
    /// </para>
    /// <list type="number">
    /// <item><b>Windows + 有 MSI</b>：下载官方安装包，<c>msiexec /qn</c> 静默安装
    /// （目录结构与 Temurin 官方安装器完全一致）；</item>
    /// <item><b>其他情况 / MSI 失败</b>：下载 zip 并解压（原本的部署方式，作为兜底）。</item>
    /// </list>
    /// </summary>
    /// <param name="item">要安装的版本。</param>
    /// <param name="targetDir">目标目录，例如 <c>runtime/jdk-21</c>。</param>
    /// <param name="onProgress">进度回调（0-100，阶段文案）。可能从后台线程调用。</param>
    /// <param name="ct">取消令牌（只在下载阶段生效；安装阶段不再响应取消）。</param>
    /// <returns>java 可执行文件全路径。</returns>
    public static async Task<string> InstallAsync(
        JavaReleaseItem item, string targetDir, Action<int, string> onProgress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        // 已经装好了就直接返回（重装同一版本不必再下 200 MB）
        var existing = FindJavaExe(targetDir);
        if (existing is not null)
        {
            onProgress(100, $"该版本已存在于 {targetDir}");
            return existing;
        }

        if (OperatingSystem.IsWindows() && item.HasInstaller)
        {
            try
            {
                return await InstallViaMsiAsync(item, targetDir, onProgress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ElevationCancelledException)
            {
                // 用户在 UAC 弹窗里点了「否」：这不是「MSI 方式不可用」，
                // 回退到 zip 只会在 Program Files 上再撞一次权限墙，不如直接把选择权交回用户。
                throw;
            }
            catch (Exception ex) when (!string.IsNullOrWhiteSpace(item.DownloadUrl))
            {
                // MSI 装不上（组策略禁用 Windows Installer、msiserver 服务被精简、
                // 目标目录被占用、UAC 被拒…）不能让用户卡死 ——
                // 回退到「下载 zip 解压」，功能完全等价，只是少一个卸载项。
                Log.Warning(ex, "MSI 安装失败，回退到 zip 解压：{Version}", item.Version);
                onProgress(1, "安装包方式失败，改用压缩包部署…");
            }
        }

        return await InstallFromZipAsync(item, targetDir, onProgress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 下载官方 MSI 并用 <c>msiexec /qn</c> 静默安装。
    ///
    /// <para>
    /// <c>/qn</c> 全程无界面，进程退出即安装结束；<c>/norestart</c> 禁止安装器自己重启机器；
    /// <c>INSTALLDIR</c> 显式指定目录，保证和 zip 方案、官方安装器三者的落地路径一致。
    /// </para>
    /// </summary>
    private static async Task<string> InstallViaMsiAsync(
        JavaReleaseItem item, string targetDir, Action<int, string> onProgress, CancellationToken ct)
    {
        // 注意这里**不调用** EnsureWritable：
        // MSI 是以管理员身份跑的（见 RunMsiexecAsync 的 runas），普通权限下探测
        // %ProgramFiles% 必然失败，会白白拦住后续的提权安装。
        // 只有当前进程已经是管理员时才顺手探一次，提前发现「路径非法 / 磁盘满」这类与权限无关的问题。
        if (Environment.IsPrivilegedProcess)
        {
            onProgress(1, "检查安装目录权限");
            EnsureWritable(targetDir);
        }

        var msiPath = Path.Combine(Path.GetTempPath(), $"vbl-jdk-{item.Major}-{Guid.NewGuid():N}.msi");

        try
        {
            onProgress(2, $"准备下载 JDK {item.Version} 安装包（{FormatSize(item.InstallerSize)}）");

            var urls = new[] { item.InstallerUrl, item.InstallerMirrorUrl }
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var ok = await ResilientFileDownloader.DownloadAsync(
                urls,
                msiPath,
                (have, total) =>
                {
                    var percent = total > 0 ? (int)Math.Clamp(have * 100 / total, 0, 100) : 0;
                    var sizeText = total > 0
                        ? $"{have / 1024d / 1024d:0.0} / {total / 1024d / 1024d:0.0} MB"
                        : $"{have / 1024d / 1024d:0.0} MB";
                    onProgress(Math.Clamp(percent, 0, 80), $"下载 JDK {item.Version} 安装包　{sizeText}");
                },
                ct).ConfigureAwait(false);

            if (!ok)
                throw new InvalidOperationException("JDK 安装包下载不完整，请重试或换个网络环境");

            var actualSize = new FileInfo(msiPath).Length;
            if (item.InstallerSize > 0 && actualSize != item.InstallerSize)
                throw new InvalidOperationException(
                    $"JDK 安装包大小不符（期望 {item.InstallerSize} 字节，实际 {actualSize} 字节）");

            if (!string.IsNullOrEmpty(item.InstallerSha256))
            {
                onProgress(82, "校验 SHA256");
                var actual = await ComputeSha256Async(msiPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, item.InstallerSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("JDK 安装包校验失败（SHA256 不匹配），请重试");
            }

            onProgress(86, "正在静默安装…（安装期间请勿关闭启动器）");
            var exitCode = await RunMsiexecAsync(msiPath, targetDir).ConfigureAwait(false);

            // 不拿退出码当唯一判据：提权（runas）启动时 shell 中转，退出码未必原样传回；
            // 反过来 1638「已安装同版本」也算成功。以「目标目录里到底有没有 java.exe」为准。
            var javaPath = FindJavaExe(targetDir)
                           ?? FindJavaExeIn(targetDir, depth: 2);

            if (javaPath is null)
                throw new InvalidOperationException(
                    $"安装程序返回 {exitCode}（{DescribeMsiExitCode(exitCode)}），"
                    + $"但 {targetDir} 下没有找到 bin\\java.exe。");

            onProgress(100, $"JDK {item.Version} 安装完成");
            Log.Information("JDK 安装完成（MSI 静默安装，退出码 {Code}）：{Version} → {Path}",
                exitCode, item.Version, javaPath);
            return javaPath;
        }
        finally
        {
            TryDelete(msiPath);
        }
    }

    /// <summary>
    /// 执行 <c>msiexec</c> 静默安装。
    ///
    /// <para>
    /// 非管理员时用 <c>runas</c> 提权：普通权限跑 <c>/qn</c> 装进 Program Files 必然 1603，
    /// 与其让用户对着「致命错误」发愣，不如让系统弹一次 UAC，点「是」就装完。
    /// </para>
    /// </summary>
    /// <returns>msiexec 退出码（提权场景下可能不可靠，调用方应以文件落地情况为准）。</returns>
    private static async Task<int> RunMsiexecAsync(string msiPath, string installDir)
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"vbl-msi-{Guid.NewGuid():N}.log");

        var args =
            $"/i \"{msiPath}\""
            + " /qn /norestart"
            + $" /l*v \"{logPath}\""
            + $" ADDLOCAL={MsiFeatures}"
            + $" INSTALLDIR=\"{installDir.TrimEnd(Path.DirectorySeparatorChar, '\\')}\"";

        var psi = new ProcessStartInfo("msiexec.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath(),
        };

        if (!Environment.IsPrivilegedProcess)
        {
            psi.UseShellExecute = true;   // runas 只能走 ShellExecute
            psi.Verb = "runas";
        }

        Log.Information("开始静默安装 JDK：msiexec {Args}", args);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 msiexec.exe");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new ElevationCancelledException(
                "已取消管理员授权，安装未进行。\n"
                + "二选一：\n"
                + "· 重新点一次安装，在 UAC 弹窗里选「是」；\n"
                + "· 或把安装目录改到不需要管理员权限的位置（例如 D:\\Java）。");
        }

        using (process)
        {
            // 安装阶段故意不支持取消：msiexec 被强杀会留下半装状态（注册表有项、文件不全），
            // 比多等一两分钟更难收拾。用户要中止就关掉启动器，MSI 自身会回滚。
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "安装超时（超过 10 分钟）。可能是杀毒软件拦截了安装程序，或系统里另有安装任务在跑。");
            }

            var code = process.ExitCode;
            var tail = TryReadLogTail(logPath, 6);
            if (!string.IsNullOrEmpty(tail))
                Log.Information("msiexec 日志尾部：{Tail}", tail);

            TryDelete(logPath);
            return code;
        }
    }

    /// <summary>把 msiexec 的常见退出码翻译成人话；未知码原样展示。</summary>
    private static string DescribeMsiExitCode(int code) => code switch
    {
        0 => "安装成功",
        1602 => "用户取消了安装",
        1603 => "系统拒绝安装（通常是权限不足，或目标目录被占用）",
        1618 => "另一个安装程序正在运行，请稍后重试",
        1619 => "安装包无法打开（下载不完整或被杀毒软件隔离）",
        1625 => "安装被系统策略禁止",
        1638 => "已经安装过同一版本",
        3010 => "安装成功，需要重启系统后生效",
        _ => "未知结果",
    };

    /// <summary>在目录及指定层数的子目录里找 java 可执行文件（MSI 有时会在 INSTALLDIR 下再套一层）。</summary>
    private static string? FindJavaExeIn(string root, int depth)
    {
        if (depth < 0 || !Directory.Exists(root))
            return null;

        var direct = FindJavaExe(root);
        if (direct is not null)
            return direct;

        try
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                var found = FindJavaExeIn(dir, depth - 1);
                if (found is not null)
                    return found;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "搜索 Java 目录失败：{Root}", root);
        }

        return null;
    }

    /// <summary>读日志文件最后几行（msiexec 的 /l*v 日志可能很长，只要尾巴）。</summary>
    private static string TryReadLogTail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path))
                return string.Empty;

            var all = File.ReadAllLines(path);
            return string.Join(" | ", all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatSize(long bytes) =>
        bytes > 0 ? $"{bytes / 1024d / 1024d:0} MB" : "未知体积";

    /// <summary>
    /// 下载 zip 并解压到 <paramref name="targetDir"/>（非 Windows，或 MSI 方案失败时的兜底）。
    /// </summary>
    private static async Task<string> InstallFromZipAsync(
        JavaReleaseItem item, string targetDir, Action<int, string> onProgress, CancellationToken ct)
    {
        var javaExeName = OperatingSystem.IsWindows() ? "java.exe" : "java";

        if (string.IsNullOrWhiteSpace(item.DownloadUrl))
            throw new InvalidOperationException("这个版本没有可用的压缩包，安装失败");

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
            Log.Information("JDK 安装完成（zip 解压）：{Version} → {Path}", item.Version, javaPath);
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

    /// <summary>
    /// 用户在 UAC 弹窗里点了「否」。
    ///
    /// <para>
    /// 单独一个类型是为了和「MSI 方式不可用」区分开：后者应该回退到 zip 解压，
    /// 前者回退只会在 Program Files 上再撞一次权限墙，纯属白折腾。
    /// </para>
    /// </summary>
    private sealed class ElevationCancelledException : Exception
    {
        public ElevationCancelledException(string message) : base(message) { }
    }
}
