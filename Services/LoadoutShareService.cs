using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Services;

/// <summary>配装分享清单里的一个文件。</summary>
public sealed class LoadoutFile
{
    /// <summary>相对实例根目录的路径，如 <c>mods/sodium.jar</c>。</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;

    /// <summary>下载地址；为空表示这个文件没能对应到 Modrinth（自制品 / 未收录），分享码里带不走内容。</summary>
    [JsonPropertyName("url")] public string? Url { get; set; }

    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>展示名。</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>Modrinth 项目 slug（可空）。</summary>
    [JsonPropertyName("project")] public string? Project { get; set; }

    [JsonIgnore] public bool Shareable => !string.IsNullOrWhiteSpace(Url);
}

/// <summary>
/// 配装分享码的载荷。这是一份**自包含的实例描述**：
/// 游戏版本 + 加载器 + 模组清单（带哈希与下载地址）+ 分享者的内存 / JVM 参数。
/// </summary>
public sealed class LoadoutManifest
{
    /// <summary>格式标识，便于将来演进时识别旧码。</summary>
    [JsonPropertyName("format")] public string Format { get; set; } = "vbl-loadout/1";

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>Minecraft 版本，如 <c>1.20.1</c>。</summary>
    [JsonPropertyName("minecraft")] public string Minecraft { get; set; } = string.Empty;

    /// <summary>加载器（Modrinth 风格小写）：<c>fabric</c> / <c>forge</c> / <c>neoforge</c> / <c>quilt</c>。</summary>
    [JsonPropertyName("loader")] public string Loader { get; set; } = string.Empty;

    /// <summary>加载器版本；为空表示「装最新的」。</summary>
    [JsonPropertyName("loaderVersion")] public string? LoaderVersion { get; set; }

    [JsonPropertyName("maxMemoryMb")] public int MaxMemoryMb { get; set; }
    [JsonPropertyName("minMemoryMb")] public int MinMemoryMb { get; set; }
    [JsonPropertyName("jvmArgs")] public string JvmArgs { get; set; } = string.Empty;

    [JsonPropertyName("files")] public List<LoadoutFile> Files { get; set; } = new();

    /// <summary>没能对应到 Modrinth、只能靠文件名记录的模组（分享码里带不走内容）。</summary>
    [JsonPropertyName("localOnly")] public List<string> LocalOnly { get; set; } = new();

    [JsonIgnore] public int ShareableCount => Files.Count(f => f.Shareable);
    [JsonIgnore] public int TotalCount => Files.Count + LocalOnly.Count;
}

/// <summary>
/// 配装分享码：把「我这个实例装了哪些模组、用什么版本和加载器」压成一段可复制的短码。
///
/// <para>
/// <b>为什么能用哈希反查</b>：Modrinth 提供 <c>/version_file/&lt;sha1&gt;</c> 接口，按文件哈希
/// 直接返回它属于哪个项目 / 版本。所以不必猜文件名，只要把本地 jar 的 SHA1 报上去，
/// 就能拿到官方的下载地址与项目归属 —— 这是整套方案能做到「体积小、可复现」的关键。
/// </para>
///
/// <para>
/// <b>编码</b>：JSON → GZip → Base64URL，前缀 <c>VBL1-</c>。加了前缀是为了让用户一眼认出
/// 这是一段分享码、也让粘贴时能自动识别（而不是把别人的 JSON 当码解）。
/// </para>
///
/// <para>
/// <b>能带走什么 / 带不走什么</b>：带得走「用哪个版本的哪个 Modrinth 模组」；带不走
/// 自制品模组、配置文件和存档（那些要么无法分发，要么体积不可控）。未收录的模组会在
/// <see cref="LoadoutManifest.LocalOnly"/> 里如实列出，导入方能看到「还差哪几个要自己找」。
/// </para>
/// </summary>
public static class LoadoutShareService
{
    private const string CodePrefix = "VBL1-";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ==================== 导出 ====================

    /// <summary>
    /// 扫描一个本地实例，生成分享清单。
    /// <paramref name="onProgress"/> 会报告「正在识别第 i/n 个文件」，因为哈希反查要逐个联网。
    /// </summary>
    public static async Task<LoadoutManifest> BuildFromInstanceAsync(
        string instanceId,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new LoadoutShareException("没有选择实例");

        var mcFolder = GameInstanceHelper.GetMinecraftFolder();
        var versionsDir = Path.Combine(mcFolder, "versions");
        var loader = GameInstanceHelper.DetectLoader(versionsDir, instanceId);
        var gameVersion = ModDependencyResolver.TryGetGameVersion(instanceId);

        var manifest = new LoadoutManifest
        {
            Name = instanceId,
            Minecraft = gameVersion,
            Loader = LoaderTag(loader),
            LoaderVersion = TryGetLoaderVersion(instanceId, loader),
            MaxMemoryMb = SettingsService.Current.MaxMemoryMb,
            MinMemoryMb = SettingsService.Current.MinMemoryMb,
            JvmArgs = SettingsService.Current.JvmArgs ?? string.Empty,
        };

        if (loader is ModLoaderKind.Vanilla)
            throw new LoadoutShareException(
                "这个实例是原版（没有装模组加载器），没有模组可以分享。请先给实例装一个加载器");

        var modsFolder = GameInstanceHelper.ResolveInstanceFolder(instanceId, "mods");
        var jars = new List<string>();
        try
        {
            if (Directory.Exists(modsFolder))
                jars.AddRange(Directory.GetFiles(modsFolder, "*.jar", SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex)
        {
            throw new LoadoutShareException($"无法读取模组目录：{ex.Message}");
        }

        if (jars.Count == 0)
            throw new LoadoutShareException(
                $"这个实例的模组目录是空的：{GameInstanceHelper.DescribeInstanceFolder(instanceId, "mods")}");

        for (var i = 0; i < jars.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var jar = jars[i];
            var fileName = Path.GetFileName(jar);
            onProgress?.Invoke(i + 1, jars.Count, fileName);

            var entry = new LoadoutFile
            {
                Path = "mods/" + fileName,
                Name = Path.GetFileNameWithoutExtension(fileName),
                Sha1 = TrySha1(jar),
                Size = TryLength(jar),
            };

            if (entry.Sha1.Length > 0)
            {
                var version = await ModrinthApi.GetVersionByHashAsync(entry.Sha1, ct);
                if (version is not null)
                {
                    // 优先取哈希**完全一致**的那个文件，拿不到再退回主文件
                    var match = version.Files.FirstOrDefault(f =>
                                    !string.IsNullOrWhiteSpace(f.Sha1)
                                    && string.Equals(f.Sha1, entry.Sha1, StringComparison.OrdinalIgnoreCase))
                                ?? version.PrimaryFile;

                    if (match is not null && !string.IsNullOrWhiteSpace(match.Url))
                    {
                        entry.Url = match.Url;
                        entry.Project = version.ProjectId;
                        if (!string.IsNullOrWhiteSpace(version.Name))
                            entry.Name = Path.GetFileNameWithoutExtension(fileName);
                    }
                }
            }

            if (entry.Shareable)
                manifest.Files.Add(entry);
            else
                manifest.LocalOnly.Add(fileName);
        }

        Serilog.Log.Information(
            "配装分享：实例 {Id} 共 {Total} 个模组，可复现 {Ok} 个，仅本地 {Local} 个",
            instanceId, jars.Count, manifest.Files.Count, manifest.LocalOnly.Count);

        return manifest;
    }

    // ==================== 编码 / 解码 ====================

    /// <summary>清单 → 分享码。</summary>
    public static string Encode(LoadoutManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var raw = Encoding.UTF8.GetBytes(json);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(raw, 0, raw.Length);
        output.Flush();

        return CodePrefix + ToBase64Url(output.ToArray());
    }

    /// <summary>清单 → JSON 文本（用于「导出为文件」）。</summary>
    public static string ToJson(LoadoutManifest manifest)
        => JsonSerializer.Serialize(manifest, PrettyOptions);

    /// <summary>分享码 → 清单。手写 / 粘贴时可以带换行与空格。</summary>
    public static LoadoutManifest Decode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new LoadoutShareException("分享码是空的");

        var cleaned = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());

        // 允许用户把整段 JSON 也粘进来（有人会顺手复制导出的文件内容）
        if (cleaned.StartsWith("{", StringComparison.Ordinal))
            return ParseJson(cleaned);

        if (cleaned.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[CodePrefix.Length..];
        else
            throw new LoadoutShareException("分享码格式不对（应以 VBL1- 开头），请检查是否复制完整");

        byte[] compressed;
        try
        {
            compressed = FromBase64Url(cleaned);
        }
        catch (Exception ex)
        {
            throw new LoadoutShareException("分享码内容已损坏（Base64 解码失败）：" + ex.Message);
        }

        try
        {
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return ParseJson(reader.ReadToEnd());
        }
        catch (LoadoutShareException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LoadoutShareException("分享码内容已损坏（解压失败）：" + ex.Message);
        }
    }

    private static LoadoutManifest ParseJson(string json)
    {
        LoadoutManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<LoadoutManifest>(json);
        }
        catch (Exception ex)
        {
            throw new LoadoutShareException("分享码里的数据读不出来：" + ex.Message);
        }

        if (manifest is null)
            throw new LoadoutShareException("分享码里没有有效的配装数据");

        if (string.IsNullOrWhiteSpace(manifest.Format) || !manifest.Format.StartsWith("vbl-loadout", StringComparison.OrdinalIgnoreCase))
            throw new LoadoutShareException($"不认识的分享码格式：{manifest.Format}");

        if (string.IsNullOrWhiteSpace(manifest.Minecraft))
            throw new LoadoutShareException("分享码里没有 Minecraft 版本信息");

        return manifest;
    }

    // ==================== 导入 ====================

    /// <summary>
    /// 按分享清单**新建一个实例**（复用整合包安装流程：装基础版本 → 装加载器 → 拉模组）。
    /// </summary>
    /// <param name="manifest">分享清单。</param>
    /// <param name="sourceLabel">来源描述，用于进度页标题。</param>
    /// <returns>新实例 id；失败返回 null（提示已弹出）。</returns>
    public static async Task<string?> ImportAsNewInstanceAsync(
        LoadoutManifest manifest,
        string sourceLabel = "配装码",
        CancellationToken ct = default)
    {
        // 没有可下载的模组、也没有游戏版本 → 真的无从下手。
        // 但「有版本、没模组」是合法的（纯原版配装）：对方只想让启动器帮他装个本体 + 加载器。
        if (manifest.ShareableCount == 0 && string.IsNullOrWhiteSpace(manifest.Minecraft))
        {
            AppServices.Notifications?.Warning(
                "这个分享码里既没有可下载的模组，也没有记录游戏版本，无法自动安装");
            return null;
        }

        var tempFile = Path.Combine(Path.GetTempPath(), "VibrantbitLauncher",
            $"share_{Guid.NewGuid():N}.mrpack");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
            await Task.Run(() => WriteModrinthPack(manifest, tempFile), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 还没开始安装就取消了：不弹错，安静退出（调用方会显示「已取消」）。
            return null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "生成配装分享安装包失败");
            AppServices.Notifications?.Error($"生成安装包失败：{ex.Message}");
            return null;
        }

        try
        {
            // 走整合包协调器 → 复用进度详情页、任务中心、完成提示。
            // 但**必须告诉它是「配装」而不是「整合包」**：任务列表类型、进度窗口标题、
            // 完成/失败提示都按这个分支，否则用户看到的是「整合包：xxx」，像是下错了东西。
            var instanceId = await ModpackInstallCoordinator.InstallAsync(
                tempFile, sourceLabel, InstallKind.Loadout, ct);
            return instanceId;
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* 临时文件删不掉无妨 */ }
        }
    }

    /// <summary>
    /// 把分享清单里的模组**下载进一个现有实例**（不新建实例、不装加载器）。
    /// </summary>
    /// <returns>成功下载的文件数。</returns>
    public static async Task<int> ImportIntoInstanceAsync(
        LoadoutManifest manifest,
        string instanceId,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new LoadoutShareException("没有选择要导入的实例");

        var files = manifest.Files.Where(f => f.Shareable).ToList();
        if (files.Count == 0)
            throw new LoadoutShareException("这个分享码里没有任何可自动下载的模组");

        var modsFolder = GameInstanceHelper.ResolveInstanceFolder(instanceId, "mods");
        Directory.CreateDirectory(modsFolder);

        var done = 0;
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            onProgress?.Invoke(i + 1, files.Count, Path.GetFileName(file.Path));

            var target = Path.Combine(modsFolder, Path.GetFileName(file.Path));

            // 已存在且哈希一致 → 跳过（幂等，重复导入不会多出一堆副本）
            if (File.Exists(target) && file.Sha1.Length > 0
                && string.Equals(TrySha1(target), file.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                done++;
                continue;
            }

            target = UniquePath(target);

            try
            {
                await DownloadAsync(file.Url!, target, ct);
                done++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "配装分享：下载失败 {Url}", file.Url);
                AppServices.Notifications?.Warning($"下载失败：{Path.GetFileName(file.Path)}（{ex.Message}）");
            }
        }

        Serilog.Log.Information("配装分享：导入到 {Id}，成功 {Done}/{Total}", instanceId, done, files.Count);
        return done;
    }

    /// <summary>把清单转成 Modrinth 规范的 <c>.mrpack</c>，交给整合包安装器。</summary>
    private static void WriteModrinthPack(LoadoutManifest manifest, string targetPath)
    {
        var files = manifest.Files.Where(f => f.Shareable).Select(f => new Dictionary<string, object?>
        {
            ["path"] = f.Path,
            ["hashes"] = string.IsNullOrWhiteSpace(f.Sha1) ? new Dictionary<string, string>() : new Dictionary<string, string> { ["sha1"] = f.Sha1 },
            ["downloads"] = new[] { f.Url },
            ["fileSize"] = f.Size,
        }).ToList();

        var dependencies = new Dictionary<string, string>
        {
            ["minecraft"] = manifest.Minecraft,
        };

        switch (manifest.Loader?.ToLowerInvariant())
        {
            case "fabric": dependencies["fabric-loader"] = manifest.LoaderVersion ?? "latest"; break;
            case "quilt": dependencies["quilt-loader"] = manifest.LoaderVersion ?? "latest"; break;
            case "forge": dependencies["forge"] = manifest.LoaderVersion ?? "latest"; break;
            case "neoforge": dependencies["neoforge"] = manifest.LoaderVersion ?? "latest"; break;
        }

        var index = new Dictionary<string, object?>
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = "1.0.0",
            ["name"] = string.IsNullOrWhiteSpace(manifest.Name) ? "Shared Loadout" : manifest.Name,
            ["files"] = files,
            ["dependencies"] = dependencies,
        };

        if (File.Exists(targetPath))
            File.Delete(targetPath);

        using var zip = ZipFile.Open(targetPath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(index, JsonOptions));
    }

    // ==================== 解析版本信息 ====================

    /// <summary>加载器枚举 → Modrinth 的依赖键 / 标签（小写）。</summary>
    public static string LoaderTag(ModLoaderKind kind) => kind switch
    {
        ModLoaderKind.Fabric => "fabric",
        ModLoaderKind.Quilt => "quilt",
        ModLoaderKind.Forge => "forge",
        ModLoaderKind.NeoForge => "neoforge",
        ModLoaderKind.LiteLoader => "liteloader",
        _ => string.Empty,
    };

    /// <summary>分享码里的加载器标签 → 本工程枚举（认不出返回 Unknown）。</summary>
    public static ModLoaderKind ParseLoaderTag(string? tag)
        => ModLoaderHelper.ParseName(tag) ?? ModLoaderKind.Unknown;

    /// <summary>
    /// 从实例的版本 JSON（含 <c>inheritsFrom</c> 链）里找出加载器正式版本号。
    /// 取不到返回 null —— 安装器会退化成「装最新的」，不影响可用性。
    /// </summary>
    public static string? TryGetLoaderVersion(string instanceId, ModLoaderKind loader)
    {
        var prefix = loader switch
        {
            ModLoaderKind.Fabric => "net.fabricmc:fabric-loader:",
            ModLoaderKind.Quilt => "org.quiltmc:quilt-loader:",
            ModLoaderKind.Forge => "net.minecraftforge:forge:",
            ModLoaderKind.NeoForge => "net.neoforged",
            _ => null,
        };

        if (prefix is null)
            return null;

        try
        {
            var mcFolder = GameInstanceHelper.GetMinecraftFolder();
            var versionsDir = Path.Combine(mcFolder, "versions");

            var current = instanceId;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var depth = 0; depth < 4 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                if (!visited.Add(current))
                    break;

                var jsonPath = Path.Combine(versionsDir, current, current + ".json");
                if (!File.Exists(jsonPath))
                    break;

                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = doc.RootElement;

                if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lib in libs.EnumerateArray())
                    {
                        if (!lib.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                            continue;

                        var name = nameEl.GetString() ?? string.Empty;
                        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var version = name[prefix.Length..];

                        // Forge 的坐标形如 net.minecraftforge:forge:1.20.1-47.2.0 —— 去掉前面的 MC 版本
                        if (loader == ModLoaderKind.Forge && version.Contains('-'))
                            version = version[(version.IndexOf('-') + 1)..];

                        // NeoForge：21.1.0 直接用；1.20.1 的坐标是 net.neoforged:forge:1.20.1-47.1.105
                        if (loader == ModLoaderKind.NeoForge && version.Contains('-'))
                            version = version[(version.IndexOf('-') + 1)..];

                        if (!string.IsNullOrWhiteSpace(version))
                            return version;
                    }
                }

                current = root.TryGetProperty("inheritsFrom", out var inh) && inh.ValueKind == JsonValueKind.String
                    ? inh.GetString() ?? string.Empty
                    : string.Empty;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "读取加载器版本失败：{Id}", instanceId);
        }

        return null;
    }

    // ==================== 小工具 ====================

    /// <summary>
    /// 下载配装清单里的一个文件。1.0.7.15 起统一走 <see cref="FileDownloader"/> ——
    /// 分块并行 + 续传 + 校验，并且会把字节数报进全局速度表（配装码导入的进度条就是靠它）。
    /// </summary>
    private static async Task DownloadAsync(string url, string targetPath, CancellationToken ct)
    {
        var item = new DownloadItem(
            url,
            targetPath,
            DisplayName: Path.GetFileName(targetPath));

        await FileDownloader.DownloadAsync(item, onBytes: null, onStatus: null, ct: ct)
            .ConfigureAwait(false);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    private static string TrySha1(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA1.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static long TryLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static string ToBase64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            0 => s,
            _ => throw new FormatException("Base64 长度非法"),
        };
        return Convert.FromBase64String(s);
    }
}

/// <summary>配装分享过程中的可展示异常。</summary>
public sealed class LoadoutShareException : Exception
{
    public LoadoutShareException(string message) : base(message) { }
    public LoadoutShareException(string message, Exception inner) : base(message, inner) { }
}
