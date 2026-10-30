using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services;

/// <summary>导出格式。</summary>
public enum PackFormat
{
    /// <summary>Modrinth 规范（<c>.mrpack</c>）：清单里只记「哪个模组、哪一版、从哪下」，体积小。</summary>
    Mrpack = 0,

    /// <summary>普通整合包（<c>.zip</c>）：模组原样装进包里，不联网、不依赖第三方站点。</summary>
    Zip = 1,
}

/// <summary>导出选项。</summary>
public sealed class PackExportOptions
{
    /// <summary>整合包名称（显示在对方启动器里）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>整合包自身的版本号，如 <c>1.0.0</c>。</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>一句话简介。</summary>
    public string Summary { get; set; } = string.Empty;

    public PackFormat Format { get; set; } = PackFormat.Mrpack;

    /// <summary>打包 config / defaultconfigs / kubejs / scripts。</summary>
    public bool IncludeConfig { get; set; } = true;

    public bool IncludeResourcePacks { get; set; }

    public bool IncludeShaderPacks { get; set; }

    /// <summary>打包存档。默认关：存档里通常有建筑、坐标与进度，属于私人物品。</summary>
    public bool IncludeSaves { get; set; }

    /// <summary>打包 options.txt（键位、视频设置）。默认关：会覆盖对方的操作习惯。</summary>
    public bool IncludeOptionsFile { get; set; }

    /// <summary>
    /// 把「Modrinth 上查不到」的模组原样塞进包里。关掉的话对方会缺这些模组。
    /// </summary>
    public bool BundleUnresolvedMods { get; set; } = true;
}

/// <summary>导出结果。</summary>
public sealed class PackExportResult
{
    public string FilePath { get; init; } = string.Empty;

    /// <summary>模组总数。</summary>
    public int ModTotal { get; set; }

    /// <summary>已对应到 Modrinth、对方启动器会自动下载的数量。</summary>
    public int ModLinked { get; set; }

    /// <summary>随包携带（写进 overrides）的数量。</summary>
    public int ModBundled { get; set; }

    /// <summary>既没链接也没打包的数量（用户关掉了「一并打包」）。</summary>
    public int ModSkipped { get; set; }

    public long SizeBytes { get; set; }

    public List<string> Warnings { get; } = new();

    public string DescribeSize()
        => SizeBytes >= 1024L * 1024 * 1024
            ? $"{SizeBytes / 1024d / 1024d / 1024d:0.##} GB"
            : SizeBytes >= 1024 * 1024
                ? $"{SizeBytes / 1024d / 1024d:0.#} MB"
                : $"{SizeBytes / 1024d:0.#} KB";
}

/// <summary>导出过程中的可展示异常。</summary>
public sealed class PackExportException : Exception
{
    public PackExportException(string message) : base(message) { }
    public PackExportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 把本地实例导出成整合包：Modrinth 规范的 <c>.mrpack</c>，或自包含的 <c>.zip</c>。
///
/// <para>
/// <b>两种格式的取舍</b>：<c>.mrpack</c> 只记录「哪个项目、哪一版、从哪下」，几十个模组也就几十 KB，
/// 对方启动器会自己下载 —— 但它依赖 Modrinth 上确实有这些文件。所以本地自制的、
/// 或没发布到 Modrinth 的模组必须**塞进 overrides 随包带走**，否则对方会缺模组。
/// <c>.zip</c> 走另一条路：不联网、全部原样打包，谁都能用，代价是体积大。
/// </para>
///
/// <para>
/// <b>为什么还要写 modrinth.index.json</b>：即使导出 <c>.zip</c>，包里也放一份清单
/// （<c>files</c> 为空、只写 <c>dependencies</c>）。这样它既能在别的启动器里当整合包导入，
/// 又能在解压后直接当一份游戏目录用 —— 两种用法都不亏。
/// </para>
///
/// <para>
/// <b>安全红线</b>：<c>launcher_accounts.json</c> 里有登录令牌，<c>usercache.json</c> 里有别人的
/// UUID，这些**永远不打包**。存档、服务器列表默认也不打包 —— 整合包是要发给别人的。
/// </para>
/// </summary>
public static class InstancePackExporter
{
    /// <summary>算作「配置」的目录，勾选「打包配置」时一起带走。</summary>
    private static readonly string[] ConfigFolders = { "config", "defaultconfigs", "kubejs", "scripts" };

    /// <summary>
    /// 永远不打包的文件。前四个含账号凭据或他人 UUID，属于必须拦住的；
    /// 后面几个是日志与缓存，打进去只会白占体积。
    /// </summary>
    private static readonly string[] NeverPackFiles =
    {
        "launcher_accounts.json", "launcher_profiles.json", "usercache.json", "usernamecache.json",
        "launcher_log.txt", "servers.dat_old", "realms_persistence.json",
    };

    /// <summary>永远不打包的目录（大小不可控，或者纯属本机痕迹）。</summary>
    private static readonly string[] NeverPackFolders =
    {
        "logs", "crash-reports", "screenshots", ".cache", ".mixin.out", "debug", "natives",
        "versions", "libraries", "assets", "runtime", "bin", "out", "backups",
    };

    /// <summary>打包时跳过的扩展名。</summary>
    private static readonly string[] SkipExtensions = { ".log", ".tmp", ".lock" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ==================== 对外入口 ====================

    /// <summary>
    /// 导出实例为整合包。
    /// </summary>
    /// <param name="instanceId">实例 id（<c>versions</c> 下的目录名）。</param>
    /// <param name="targetPath">目标文件路径（.mrpack 或 .zip）。</param>
    /// <param name="options">导出选项。</param>
    /// <param name="onStatus">进度文案回调（逐个模组识别 / 打包文件时调用）。</param>
    public static async Task<PackExportResult> ExportAsync(
        string instanceId,
        string targetPath,
        PackExportOptions options,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new PackExportException("没有选择实例");

        if (string.IsNullOrWhiteSpace(targetPath))
            throw new PackExportException("没有选择保存位置");

        var mcFolder = GameInstanceHelper.GetMinecraftFolder();
        var versionsDir = Path.Combine(mcFolder, "versions");
        var loader = GameInstanceHelper.DetectLoader(versionsDir, instanceId);
        var gameVersion = ModDependencyResolver.TryGetGameVersion(instanceId);

        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            throw new PackExportException(
                "读不出这个实例的游戏版本，导出的整合包对方会不知道装哪个版本。请确认这个实例能正常启动一次");
        }

        // 版本隔离关闭时实例与根目录是同一层 —— 这时 config/mods 本来就在根目录，导出的是整份游戏目录。
        var instanceRoot = Path.TrimEndingDirectorySeparator(
            GameInstanceHelper.ResolveInstanceFolder(instanceId, string.Empty));

        var result = new PackExportResult { FilePath = targetPath };
        var modsDir = Path.Combine(instanceRoot, "mods");

        // ---------- 1. 收集模组，算哈希 ----------
        var jars = new List<string>();
        try
        {
            if (Directory.Exists(modsDir))
                jars.AddRange(Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex)
        {
            throw new PackExportException($"无法读取模组目录：{ex.Message}", ex);
        }

        result.ModTotal = jars.Count;
        var modEntries = new List<PackModEntry>();

        for (var i = 0; i < jars.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var jar = jars[i];
            var fileName = Path.GetFileName(jar);
            onStatus?.Invoke($"正在校验模组（{i + 1}/{jars.Count}）：{fileName}");

            var entry = new PackModEntry
            {
                FileName = fileName,
                FullPath = jar,
                Sha1 = TryHash(jar, SHA1.Create),
                Sha512 = TryHash(jar, SHA512.Create),
                Size = TryLength(jar),
            };

            // 只有 mrpack 需要联网反查；zip 全部随包携带，不用问 Modrinth
            if (options.Format == PackFormat.Mrpack && entry.Sha1.Length > 0)
            {
                try
                {
                    var version = await ModrinthApi.GetVersionByHashAsync(entry.Sha1, ct);
                    if (version is not null)
                    {
                        var match = version.Files.FirstOrDefault(f =>
                                        !string.IsNullOrWhiteSpace(f.Sha1)
                                        && string.Equals(f.Sha1, entry.Sha1, StringComparison.OrdinalIgnoreCase))
                                    ?? version.PrimaryFile;

                        if (match is not null && !string.IsNullOrWhiteSpace(match.Url))
                        {
                            entry.Url = match.Url;
                            entry.ProjectId = version.ProjectId;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单个模组查不到不影响整体 —— 后面会退化成「随包携带」
                    Serilog.Log.Debug(ex, "整合包导出：模组哈希反查失败 {File}", fileName);
                }
            }

            modEntries.Add(entry);
        }

        // ---------- 2. 决定每个模组怎么处理 ----------
        foreach (var entry in modEntries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Url))
            {
                entry.Disposition = ModDisposition.Linked;
                result.ModLinked++;
            }
            else if (options.BundleUnresolvedMods)
            {
                entry.Disposition = ModDisposition.Bundled;
                result.ModBundled++;
            }
            else
            {
                entry.Disposition = ModDisposition.Skipped;
                result.ModSkipped++;
                result.Warnings.Add($"{entry.FileName}：Modrinth 上查不到，又没有选择一并打包，对方会缺这个模组");
            }
        }

        var loaderVersion = LoadoutShareService.TryGetLoaderVersion(instanceId, loader);

        // ---------- 3. 写包 ----------
        var tempPath = targetPath + ".part";
        try
        {
            await Task.Run(() => WriteArchive(
                tempPath, instanceRoot, gameVersion, loader, loaderVersion,
                modEntries, options, result, onStatus, ct), ct);

            if (File.Exists(targetPath))
                File.Delete(targetPath);

            File.Move(tempPath, targetPath);
            result.SizeBytes = TryLength(targetPath);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (PackExportException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            throw new PackExportException($"打包失败：{ex.Message}", ex);
        }

        Serilog.Log.Information(
            "导出整合包：{Id} → {Path}（模组 {Total} 个，链接 {Linked}，随包 {Bundled}，跳过 {Skipped}）",
            instanceId, targetPath, result.ModTotal, result.ModLinked, result.ModBundled, result.ModSkipped);

        return result;
    }

    // ==================== 写 ZIP ====================

    private static void WriteArchive(
        string targetPath,
        string instanceRoot,
        string gameVersion,
        ModLoaderKind loader,
        string? loaderVersion,
        List<PackModEntry> modEntries,
        PackExportOptions options,
        PackExportResult result,
        Action<string>? onStatus,
        CancellationToken ct)
    {
        using var zip = ZipFile.Open(targetPath, ZipArchiveMode.Create);

        // ---------- modrinth.index.json ----------
        var files = modEntries
            .Where(e => e.Disposition == ModDisposition.Linked)
            .Select(e => new Dictionary<string, object?>
            {
                ["path"] = "mods/" + e.FileName,
                ["hashes"] = new Dictionary<string, string>
                {
                    ["sha1"] = e.Sha1,
                    ["sha512"] = e.Sha512,
                },
                // 规范要求 env 存在；模组对客户端与服务端都是必须的
                ["env"] = new Dictionary<string, string>
                {
                    ["client"] = "required",
                    ["server"] = "required",
                },
                ["downloads"] = new[] { e.Url },
                ["fileSize"] = e.Size,
            })
            .ToList();

        var dependencies = new Dictionary<string, string>
        {
            ["minecraft"] = gameVersion,
        };

        // 加载器键必须写成「具体版本」才符合规范；取不到时退回 latest，
        // 本工程的安装器认识这个值，别的启动器可能只装本体 —— 所以下面会记一条警告。
        var loaderKey = LoadoutShareService.LoaderTag(loader);
        if (loaderKey.Length > 0)
        {
            dependencies[loaderKey] = string.IsNullOrWhiteSpace(loaderVersion) ? "latest" : loaderVersion;
            if (string.IsNullOrWhiteSpace(loaderVersion))
                result.Warnings.Add("没读到这个实例的加载器版本号，清单里写的是 latest，对方启动器可能需要自己选一下版本");
        }
        else if (loader is ModLoaderKind.Vanilla)
        {
            result.Warnings.Add("这是原版实例，导出包里不含加载器，对方装上就能直接玩");
        }

        var index = new Dictionary<string, object?>
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = string.IsNullOrWhiteSpace(options.Version) ? "1.0.0" : options.Version.Trim(),
            ["name"] = string.IsNullOrWhiteSpace(options.Name) ? "Minecraft 整合包" : options.Name.Trim(),
            ["summary"] = string.IsNullOrWhiteSpace(options.Summary) ? string.Empty : options.Summary.Trim(),
            ["files"] = files,
            ["dependencies"] = dependencies,
        };

        onStatus?.Invoke("正在写入整合包清单…");
        var indexEntry = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(indexEntry.Open(), new UTF8Encoding(false)))
            writer.Write(JsonSerializer.Serialize(index, JsonOptions));

        // ---------- overrides ----------
        // mrpack 的约定：清单里没写的东西，全部放在 overrides/ 下原样铺开。
        // 我们的 .zip 也照这个结构走，所以两个格式的内部布局完全一致，只是 mods 的放法不同。

        // ① 随包携带的模组
        foreach (var entry in modEntries.Where(e => e.Disposition == ModDisposition.Bundled))
        {
            ct.ThrowIfCancellationRequested();
            onStatus?.Invoke($"正在打包模组：{entry.FileName}");
            AddFile(zip, entry.FullPath, "overrides/mods/" + entry.FileName);
        }

        // ② 配置类目录
        if (options.IncludeConfig)
        {
            foreach (var folder in ConfigFolders)
            {
                ct.ThrowIfCancellationRequested();
                AddDirectory(zip, instanceRoot, folder, "overrides/" + folder, onStatus, ct);
            }
        }

        // ③ 资源包 / 光影
        if (options.IncludeResourcePacks)
            AddDirectory(zip, instanceRoot, "resourcepacks", "overrides/resourcepacks", onStatus, ct);

        if (options.IncludeShaderPacks)
            AddDirectory(zip, instanceRoot, "shaderpacks", "overrides/shaderpacks", onStatus, ct);

        // ④ 存档
        if (options.IncludeSaves)
            AddDirectory(zip, instanceRoot, "saves", "overrides/saves", onStatus, ct);

        // ⑤ 选项文件
        if (options.IncludeOptionsFile)
        {
            var optionsFile = Path.Combine(instanceRoot, "options.txt");
            if (File.Exists(optionsFile))
                AddFile(zip, optionsFile, "overrides/options.txt");
        }
    }

    // ==================== 加文件 / 加目录 ====================

    private static void AddFile(ZipArchive zip, string source, string entryName)
    {
        try
        {
            zip.CreateEntryFromFile(source, entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        }
        catch (Exception ex)
        {
            // 单个文件被占用 / 没权限，不该让整次导出失败
            Serilog.Log.Debug(ex, "整合包导出：跳过文件 {File}", source);
        }
    }

    private static void AddDirectory(
        ZipArchive zip,
        string instanceRoot,
        string relativeFolder,
        string entryPrefix,
        Action<string>? onStatus,
        CancellationToken ct)
    {
        var sourceDir = Path.Combine(instanceRoot, relativeFolder);
        if (!Directory.Exists(sourceDir))
            return;

        var name = Path.GetFileName(relativeFolder);
        if (name.Length == 0)
            return;

        onStatus?.Invoke($"正在打包 {name}…");

        List<string> files;
        try
        {
            // AttributesToSkip 必须带上 ReparsePoint：本项目用「目录链接」把 resourcepacks / saves
            // 挪到别的盘，顺着链接走会把整个外部目录（甚至盘符）打进来，链接成环时还会死循环。
            // IgnoreInaccessible 让单个没权限的子目录不至于把整次导出带崩。
            files = Directory.GetFiles(sourceDir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            }).ToList();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "整合包导出：目录遍历失败 {Dir}", sourceDir);
            return;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(file);
            if (SkipExtensions.Any(s => s.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                continue;

            var fileName = Path.GetFileName(file);
            if (NeverPackFiles.Any(s => s.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                continue;

            // 相对实例根的路径，统一成正斜杠
            var relative = Path.GetRelativePath(instanceRoot, file).Replace('\\', '/');
            AddFile(zip, file, $"{entryPrefix}/{relative[(relative.IndexOf('/') + 1)..]}");
        }
    }

    // ==================== 小工具 ====================

    private static string TryHash(string path, Func<HashAlgorithm> factory)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var hash = factory();
            return Convert.ToHexString(hash.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static long TryLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 删不掉就算了，别盖住真正的错误 */ }
    }

    /// <summary>是否应该排除某个顶层目录（供界面估算体积时复用）。</summary>
    public static bool IsNeverPacked(string name)
        => NeverPackFolders.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>导出过程中的一个模组。</summary>
    private sealed class PackModEntry
    {
        public string FileName { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public string Sha1 { get; init; } = string.Empty;
        public string Sha512 { get; init; } = string.Empty;
        public long Size { get; init; }

        /// <summary>Modrinth 上的下载地址；为空表示没对上。</summary>
        public string? Url { get; set; }

        public string ProjectId { get; set; } = string.Empty;

        public ModDisposition Disposition { get; set; } = ModDisposition.Skipped;
    }

    private enum ModDisposition
    {
        /// <summary>清单里只写一行，对方自己去下。</summary>
        Linked,

        /// <summary>打进 overrides/mods。</summary>
        Bundled,

        /// <summary>丢弃（用户没勾「一并打包」）。</summary>
        Skipped,
    }
}
