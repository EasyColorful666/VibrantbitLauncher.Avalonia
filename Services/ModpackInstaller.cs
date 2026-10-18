using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MinecraftLaunch.Base.Models.Network;
using MinecraftLaunch.Components.Parser;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 整合包安装器：把 Modrinth 的 <c>.mrpack</c> 与 CurseForge 的 <c>.zip</c> 安装成一个
/// 独立的 Minecraft 实例。
///
/// <para>
/// <b>为什么不是「往实例里拷文件」</b>：整合包带着「用哪个 Minecraft 版本 + 哪个加载器 +
/// 哪个加载器版本」的完整描述，它要求的是**新建一个实例**，而不是往已有实例塞东西。
/// 所以这里的流程是：解包 → 读清单 → 装基础版本 → 装加载器 → 拉清单里的文件 → 覆盖 overrides。
/// </para>
///
/// <para>
/// <b>两种格式</b>：
/// <list type="bullet">
/// <item><b>Modrinth <c>.mrpack</c></b>：ZIP 根目录有 <c>modrinth.index.json</c>，
/// 里面的 <c>dependencies</c> 直接给出 <c>minecraft</c> / <c>fabric-loader</c> /
/// <c>quilt-loader</c> / <c>forge</c> / <c>neoforge</c> 的版本号；<c>files[]</c> 是待下载文件；
/// <c>overrides/</c> 是随包携带的（配置、资源等）。</item>
/// <item><b>CurseForge <c>.zip</c></b>：根目录有 <c>manifest.json</c>，
/// <c>minecraft.version</c> + <c>modLoaders[]</c> 给出基础版本与加载器；
/// <c>files[]</c> 只给 CurseForge 的 projectID/fileID（要查 API 才有下载地址，本实现不强求，
/// 拿不到就跳过并记录），<c>overrides/</c> 同样是随包内容。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>安全</b>：整合包内容是**外部输入**，必须防目录穿越 —— 清单里的每个 <c>path</c>
/// 都要确认解析后仍在实例目录内；ZIP 解包也不能信任条目名。见 <see cref="ResolveInside"/>。
/// </para>
/// </summary>
public static class ModpackInstaller
{
    // ==================== 对外模型 ====================

    /// <summary>整合包格式。</summary>
    public enum ModpackFormat
    {
        /// <summary>不认识的包。</summary>
        Unknown = 0,

        /// <summary>Modrinth <c>.mrpack</c>。</summary>
        Modrinth,

        /// <summary>CurseForge <c>.zip</c>。</summary>
        CurseForge
    }

    /// <summary>整合包清单（与具体格式无关的统一视图）。</summary>
    public sealed class ModpackManifest
    {
        public ModpackFormat Format { get; init; }

        /// <summary>整合包名字（用于给新实例起名）。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>版本描述（可空，仅用于展示）。</summary>
        public string? Version { get; init; }

        /// <summary>需要的 Minecraft 版本，如 <c>1.20.1</c>。</summary>
        public string MinecraftVersion { get; init; } = string.Empty;

        /// <summary>需要的加载器，如 <c>Fabric</c> / <c>Forge</c> / <c>NeoForge</c> / <c>Quilt</c>。</summary>
        public ModLoaderKind Loader { get; init; } = ModLoaderKind.Vanilla;

        /// <summary>加载器版本（Modrinth 会给；CurseForge 也会给，但有些包写 <c>latest</c>）。</summary>
        public string? LoaderVersion { get; init; }

        /// <summary>要下载的文件（相对实例根目录的路径 + 下载地址）。</summary>
        public List<ModpackFile> Files { get; init; } = new();

        /// <summary>随包携带的覆盖层目录名（按顺序叠加：前面的先铺，后面的覆盖前面）。</summary>
        public List<string> OverrideFolders { get; init; } = new();

        /// <summary>解析时遇到的、不影响安装但值得告诉用户的问题。</summary>
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>清单里的一条待下载文件。</summary>
    public sealed class ModpackFile
    {
        /// <summary>相对实例根目录的路径，如 <c>mods/xxx.jar</c>（已做过防穿越校验）。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>下载地址（HTTPS）。</summary>
        public string Url { get; init; } = string.Empty;

        /// <summary>期望大小（可为 0）。</summary>
        public long Size { get; init; }
    }

    /// <summary>
    /// 安装阶段。进度详情页按它高亮「当前在做什么」，
    /// 顺序与 <see cref="InstallAsync"/> 的流程一致（原版包会跳过 <see cref="LoaderInstall"/>）。
    /// </summary>
    public enum ModpackPhase
    {
        /// <summary>解析清单、检查目标目录。</summary>
        Preparing = 0,

        /// <summary>安装整合包要求的 Minecraft 基础版本。</summary>
        GameInstall,

        /// <summary>安装加载器（Fabric / Quilt / Forge / NeoForge）。</summary>
        LoaderInstall,

        /// <summary>下载清单 <c>files[]</c> 里的文件。</summary>
        DownloadFiles,

        /// <summary>铺开随包携带的 overrides。</summary>
        Overrides,

        /// <summary>规范化版本 JSON 并校验实例可被识别。</summary>
        Verifying,

        /// <summary>已结束（成功或失败都要从进度页切走）。</summary>
        Done
    }

    /// <summary>安装进度回调的载荷。</summary>
    public sealed class ModpackProgress
    {
        /// <summary>0~100。</summary>
        public int Percent { get; init; }

        /// <summary>当前步骤文案。</summary>
        public string Step { get; init; } = string.Empty;

        /// <summary>速度文案（可空）。</summary>
        public string Speed { get; init; } = string.Empty;

        /// <summary>当前处于哪个阶段。</summary>
        public ModpackPhase Phase { get; init; } = ModpackPhase.Preparing;

        // ---- 以下字段只在 <see cref="ModpackPhase.DownloadFiles"/> 阶段有意义（文件级进度）----

        /// <summary>已处理完成的文件数。</summary>
        public int FilesDone { get; init; }

        /// <summary>待下载文件总数。</summary>
        public int FilesTotal { get; init; }

        /// <summary>刚刚开始下载的文件（相对实例根的路径）。</summary>
        public string? StartedFile { get; init; }

        /// <summary>刚刚下载成功的文件。</summary>
        public string? CompletedFile { get; init; }

        /// <summary>刚刚失败的文件。</summary>
        public string? FailedFile { get; init; }

        /// <summary>失败原因（与 <see cref="FailedFile"/> 配对）。</summary>
        public string? FailedReason { get; init; }
    }

    /// <summary>安装结果。</summary>
    public sealed class ModpackInstallResult
    {
        /// <summary>新建实例的版本 id。</summary>
        public string InstanceId { get; init; } = string.Empty;

        /// <summary>装进去的文件数与随包文件数。</summary>
        public int DownloadedFiles { get; init; }
        public int OverriddenFiles { get; init; }

        /// <summary>被跳过的文件（缺少下载地址 / 校验不符 / 单文件失败）。</summary>
        public List<string> Skipped { get; init; } = new();

        /// <summary>解析阶段的提示。</summary>
        public List<string> Warnings { get; init; } = new();
    }

    // ==================== 常量 ====================

    private static readonly TimeSpan FileTimeout = TimeSpan.FromSeconds(120);

    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(3),
        MaxConnectionsPerServer = 8,
    })
    {
        // 总超时交给每请求的 CTS；HttpClient 默认的 100s 罩不住响应体读到一半
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>CurseForge 的 modLoader id → 加载器种类。</summary>
    private static readonly Dictionary<string, ModLoaderKind> CurseLoaderMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["forge"] = ModLoaderKind.Forge,
        ["fabric"] = ModLoaderKind.Fabric,
        ["quilt"] = ModLoaderKind.Quilt,
        ["neoforge"] = ModLoaderKind.NeoForge,
        ["liteloader"] = ModLoaderKind.LiteLoader,
    };

    // ==================== 格式识别 ====================

    /// <summary>
    /// 按扩展名 + 内容识别整合包格式。识别不出来返回 <see cref="ModpackFormat.Unknown"/>。
    /// 只看扩展名会误判（<c>.mrpack</c> / <c>.zip</c> 都是 ZIP），所以有 ZIP 就实际读一下清单文件。
    /// </summary>
    public static ModpackFormat DetectFormat(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return ModpackFormat.Unknown;

        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            if (FindEntry(archive, "modrinth.index.json") is not null)
                return ModpackFormat.Modrinth;
            if (FindEntry(archive, "manifest.json") is not null)
                return ModpackFormat.CurseForge;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "识别整合包格式失败：{File}", filePath);
        }

        return ModpackFormat.Unknown;
    }

    /// <summary>是不是可能的整合包文件（用于拖入时快速过滤，避免对随便一个 zip 都去解析）。</summary>
    public static bool LooksLikeModpack(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var ext = Path.GetExtension(filePath);
        if (ext.Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
            return true;

        return ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
               && DetectFormat(filePath) != ModpackFormat.Unknown;
    }

    // ==================== 解析清单 ====================

    /// <summary>
    /// 解析整合包清单（只读，不解包、不下载）。
    /// 解析失败时抛 <see cref="ModpackException"/>，消息可直接展示给用户。
    /// </summary>
    public static ModpackManifest ReadManifest(string filePath)
    {
        var format = DetectFormat(filePath);
        return format switch
        {
            ModpackFormat.Modrinth => ReadModrinthManifest(filePath),
            ModpackFormat.CurseForge => ReadCurseForgeManifest(filePath),
            _ => throw new ModpackException(
                "无法识别的整合包格式。支持 Modrinth 的 .mrpack，以及 CurseForge 导出的 .zip。"
                + "（.zip 里需要有 manifest.json）")
        };
    }

    private static ModpackManifest ReadModrinthManifest(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var entry = FindEntry(archive, "modrinth.index.json")
            ?? throw new ModpackException("整合包里没有找到 modrinth.index.json，可能不是有效的 Modrinth 整合包");

        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var warnings = new List<string>();

        var name = GetString(root, "name") ?? Path.GetFileNameWithoutExtension(filePath);
        var version = GetString(root, "versionId");
        var mc = string.Empty;
        var loader = ModLoaderKind.Vanilla;
        string? loaderVersion = null;
        var loaderName = "原版";

        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
        {
            mc = GetString(deps, "minecraft") ?? string.Empty;

            // Modrinth 的 dependencies 用固定键名表示加载器
            if (TryGetString(deps, "fabric-loader", out var fabric))
            {
                loader = ModLoaderKind.Fabric; loaderVersion = fabric; loaderName = "Fabric";
            }
            else if (TryGetString(deps, "quilt-loader", out var quilt))
            {
                loader = ModLoaderKind.Quilt; loaderVersion = quilt; loaderName = "Quilt";
            }
            else if (TryGetString(deps, "neoforge", out var neo))
            {
                // NeoForge 必须先于 Forge 判（键名不同，但保持与 GameInstanceHelper 一致的顺序习惯）
                loader = ModLoaderKind.NeoForge; loaderVersion = neo; loaderName = "NeoForge";
            }
            else if (TryGetString(deps, "forge", out var forge))
            {
                loader = ModLoaderKind.Forge; loaderVersion = forge; loaderName = "Forge";
            }
        }

        if (string.IsNullOrWhiteSpace(mc))
            throw new ModpackException($"整合包「{name}」没有声明 Minecraft 版本，无法安装");

        var files = new List<ModpackFile>();
        if (root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            var skipped = 0;
            foreach (var f in filesEl.EnumerateArray())
            {
                var path = GetString(f, "path");
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                // env 里标注 client=unsupported 的文件（一般是服务端专用模组）不进客户端实例
                if (f.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object
                    && TryGetString(env, "client", out var client)
                    && client.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var url = FirstDownloadUrl(f);
                if (string.IsNullOrWhiteSpace(url))
                {
                    skipped++;
                    continue;
                }

                long size = 0;
                if (f.TryGetProperty("fileSize", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number)
                    size = sizeEl.GetInt64();

                files.Add(new ModpackFile { Path = path, Url = url!, Size = size });
            }

            if (skipped > 0)
                warnings.Add($"有 {skipped} 个文件在清单里没有下载地址，已跳过");
        }

        if (files.Count == 0)
            warnings.Add("这个整合包清单里没有需要下载的模组文件（可能全靠 overrides 随包携带）");

        // Modrinth 的覆盖层优先级：overrides < client-overrides（server-overrides 是服务端的，客户端不铺）
        var overrideFolders = new List<string>();
        if (HasFolder(archive, "overrides"))
            overrideFolders.Add("overrides");
        if (HasFolder(archive, "client-overrides"))
            overrideFolders.Add("client-overrides");

        Serilog.Log.Information(
            "解析 Modrinth 整合包：{Name} ({Mc} / {Loader} {LoaderVer})，待下载 {Count} 个文件，覆盖层 [{Overrides}]",
            name, mc, loaderName, loaderVersion ?? "?", files.Count, string.Join(", ", overrideFolders));

        return new ModpackManifest
        {
            Format = ModpackFormat.Modrinth,
            Name = name,
            Version = version,
            MinecraftVersion = mc,
            Loader = loader,
            LoaderVersion = loaderVersion,
            Files = files,
            OverrideFolders = overrideFolders,
            Warnings = warnings,
        };
    }

    private static ModpackManifest ReadCurseForgeManifest(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var entry = FindEntry(archive, "manifest.json")
            ?? throw new ModpackException("整合包里没有找到 manifest.json，可能不是有效的 CurseForge 整合包");

        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var warnings = new List<string>();
        var name = GetString(root, "name") ?? Path.GetFileNameWithoutExtension(filePath);
        var version = GetString(root, "version");
        var mc = string.Empty;
        var loader = ModLoaderKind.Vanilla;
        string? loaderVersion = null;

        if (root.TryGetProperty("minecraft", out var minecraft) && minecraft.ValueKind == JsonValueKind.Object)
        {
            mc = GetString(minecraft, "version") ?? string.Empty;

            if (minecraft.TryGetProperty("modLoaders", out var loaders) && loaders.ValueKind == JsonValueKind.Array)
            {
                // 优先取 primary=true 的那个；没有 primary 就取第一个
                JsonElement? chosen = null;
                foreach (var l in loaders.EnumerateArray())
                {
                    if (l.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True)
                    {
                        chosen = l;
                        break;
                    }
                    chosen ??= l;
                }

                if (chosen is { } ldr)
                {
                    var id = GetString(ldr, "id") ?? string.Empty;   // 形如 "fabric-0.15.11"
                    var (kind, ver) = ParseCurseLoaderId(id);
                    loader = kind;
                    loaderVersion = ver;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(mc))
            throw new ModpackException($"整合包「{name}」没有声明 Minecraft 版本，无法安装");

        // CurseForge 的 files[] 只给 projectID/fileID，真实下载地址要查 API。
        // 这里不联网取地址（需要 API Key，且包可能已经被作者删除），只统计数量告知用户。
        var fileCount = 0;
        if (root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
            fileCount = filesEl.GetArrayLength();

        if (fileCount > 0)
        {
            warnings.Add(
                $"CurseForge 整合包里的 {fileCount} 个模组需要登录 CurseForge 才能自动下载，"
                + "本次安装会先建好实例与加载器，这些模组请手动补齐");
        }

        // CurseForge 的覆盖层固定叫 overrides
        var overrideFolders = new List<string>();
        if (HasFolder(archive, "overrides"))
            overrideFolders.Add("overrides");

        Serilog.Log.Information(
            "解析 CurseForge 整合包：{Name} ({Mc} / {Loader} {LoaderVer})，清单文件 {Count} 个",
            name, mc, ModLoaderHelper.DisplayNameOf(loader), loaderVersion ?? "?", fileCount);

        return new ModpackManifest
        {
            Format = ModpackFormat.CurseForge,
            Name = name,
            Version = version,
            MinecraftVersion = mc,
            Loader = loader,
            LoaderVersion = loaderVersion,
            Files = new List<ModpackFile>(),
            OverrideFolders = overrideFolders,
            Warnings = warnings,
        };
    }

    /// <summary>CurseForge 的 <c>loader.id</c>（如 <c>fabric-0.15.11</c>）→ 种类 + 版本。</summary>
    private static (ModLoaderKind Kind, string? Version) ParseCurseLoaderId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return (ModLoaderKind.Vanilla, null);

        var dash = id.LastIndexOf('-');
        if (dash <= 0)
        {
            // 没有「-」：可能是裸加载器名（"forge"），也可能是没认出来的写法。
            // 认不出时返回 Vanilla 是**有意的** —— 上层会只装原版并给出提示，
            // 比猜一个加载器出来、把玩家的包装坏要好。
            return (CurseLoaderMap.TryGetValue(id, out var bare) ? bare : ModLoaderKind.Vanilla, null);
        }

        var name = id[..dash];
        var ver = id[(dash + 1)..];

        // ⚠️ 版本号本身可能带连字符（如 "21.1.0-beta"），LastIndexOf 只切掉最后一段，
        // 会把 name 切成 "neoforge-21.1.0"，再查表就查不到 → 静默降级成「只装原版」。
        // 这里改成**从左边第一个 '-' 切**：加载器名永远不含 '-'，版本号才可能含。
        var firstDash = id.IndexOf('-');
        if (firstDash > 0)
        {
            name = id[..firstDash];
            ver = id[(firstDash + 1)..];
        }

        // ⚠️ Forge 与 NeoForge 都是 Forge 系；NeoForge 必须**先**判，
        // 否则 "neoforge-21.1.0" 会被当成 Forge（CurseLoaderMap 里两个键都在，
        // 这里是显式把顺序摆明，避免以后有人把表改成前缀匹配时踩坑）。
        if (name.Equals("neoforge", StringComparison.OrdinalIgnoreCase))
            ver = StripLatest(ver) ?? null!;
        else if (name.Equals("forge", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("fabric", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("quilt", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("liteloader", StringComparison.OrdinalIgnoreCase))
            ver = StripLatest(ver) ?? null!;
        else
            return (ModLoaderKind.Vanilla, null);

        return (CurseLoaderMap[name], ver);
    }

    /// <summary><c>latest</c> / <c>recommended</c> 不是真实版本号，交给上层「选最新」。</summary>
    private static string? StripLatest(string ver) =>
        ver.Equals("latest", StringComparison.OrdinalIgnoreCase)
        || ver.Equals("recommended", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(ver)
            ? null
            : ver;

    // ==================== 安装 ====================

    /// <summary>
    /// 安装整合包：新建一个实例，装好基础版本 + 加载器 + 清单里的文件 + overrides。
    /// </summary>
    /// <param name="filePath">整合包文件路径（.mrpack 或 .zip）。</param>
    /// <param name="onProgress">进度回调。**可能在后台线程被调用**，调用方自行切回 UI 线程。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<ModpackInstallResult> InstallAsync(
        string filePath,
        Action<ModpackProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new ModpackException("整合包文件不存在或不可读");

        var manifest = ReadManifest(filePath);
        var mcFolder = GameInstanceHelper.GetMinecraftFolder();

        if (string.IsNullOrWhiteSpace(mcFolder))
            throw new ModpackException("还没有配置 .minecraft 目录，请先到「设置 → 主页」选择游戏目录");

        var skipped = new List<string>();
        var warnings = new List<string>(manifest.Warnings);

        // 当前阶段。只在主流程（单线程）上切换，下载并行期间固定为 DownloadFiles，
        // 所以闭包捕获 + 多线程读取是安全的。
        var phase = ModpackPhase.Preparing;

        // 文件级进度：下载阶段会并行跑，这里用原子读写。
        var filesDone = 0;
        var filesTotal = manifest.Files.Count;

        void Report(
            int percent,
            string step,
            string speed = "",
            string? startedFile = null,
            string? completedFile = null,
            string? failedFile = null,
            string? failedReason = null) =>
            onProgress?.Invoke(new ModpackProgress
            {
                Percent = percent,
                Step = step,
                // 没显式传速度时取全局速度表的值：下载阶段（文件 / 加载器库）都会往那里报字节，
                // 这样详情页在任何阶段都有实时速度，不用每个调用点自己拼 MB/s。
                Speed = string.IsNullOrEmpty(speed) ? DownloadSpeedometer.FormatSpeed() : speed,
                Phase = phase,
                FilesDone = Volatile.Read(ref filesDone),
                FilesTotal = filesTotal,
                StartedFile = startedFile,
                CompletedFile = completedFile,
                FailedFile = failedFile,
                FailedReason = failedReason,
            });

        // 下载阶段占总进度的 60%~90%，按已完成文件数线性插值（并行下载时是估算值）。
        int PercentOfFiles() =>
            60 + (int)((double)Volatile.Read(ref filesDone) / Math.Max(1, filesTotal) * 30);

        ct.ThrowIfCancellationRequested();

        // ---------- 1. 准备 ----------
        phase = ModpackPhase.Preparing;
        Report(2, $"准备安装 {manifest.Name}");

        // 实例 id：整合包名 + 加载器 + 版本，重名自动加序号。
        // 名字里可能有中文/空格/特殊字符，全部清洗成安全的目录名。
        var instanceId = BuildInstanceId(mcFolder, manifest);
        Serilog.Log.Information("整合包安装：{Name} → 实例 {Id}", manifest.Name, instanceId);

        // 整合包里的 mods / config / saves 等「实例资源」应该落在哪？
        // 必须与启动时的 gameDir 同源（RunPageViewModel：VersionIsolation ? versions/<id> : mcFolder），
        // 否则开着版本隔离时，文件下进了 .minecraft/mods，游戏却去 versions/<id>/mods 找 —— 玩家看到的是「装了没用」。
        var gameRoot = ResolveGameRoot(mcFolder, instanceId);
        Serilog.Log.Information("整合包资源落点：{Root}（版本隔离 {Iso}）", gameRoot, SettingsService.Current.VersionIsolation);

        // ---------- 2. 装 Minecraft 基础版本 ----------
        // 复用原版下载器：它会走「官方 ↔ BMCLAPI 双源 + 重试 + JSON 规范化」，
        // 老版本（1.13 以前）也能装（见 VanillaDownloader 注释）。
        phase = ModpackPhase.GameInstall;
        Report(6, $"安装 Minecraft {manifest.MinecraftVersion}");
        await EnsureVanillaAsync(mcFolder, manifest.MinecraftVersion, ct, p =>
            Report(6 + (int)(p * 0.30), $"安装 Minecraft {manifest.MinecraftVersion}"));

        ct.ThrowIfCancellationRequested();

        // ---------- 3. 装加载器 ----------
        if (manifest.Loader is not (ModLoaderKind.Vanilla or ModLoaderKind.Unknown))
        {
            var loaderName = ModLoaderHelper.DisplayNameOf(manifest.Loader);
            phase = ModpackPhase.LoaderInstall;
            Report(38, $"安装 {loaderName}");

            // 清掉 versions/ 下没有版本 JSON 的空壳目录。
            //
            // ⚠️ 为什么每次装加载器前都要扫一遍：库的 `MinecraftParser.GetMinecrafts()`
            // 会遍历 versions/ 下的**每一个**子目录并解析，只要有一个没有版本 JSON
            // 就直接抛 FileNotFoundException（MinecraftParser.Parse 里
            // `clientDir.GetFiles(clientDir.Name + ".json").FirstOrDefault() ?? throw`）。
            // 历史失败残留、或用户手动建的半截目录都会踩到，一旦踩到，
            // 后面「校验实例 / 解析版本」这些步骤会连带崩。
            PurgeBrokenInstanceDirs(mcFolder);

            switch (manifest.Loader)
            {
                case ModLoaderKind.Fabric:
                    {
                        var ver = manifest.LoaderVersion
                            ?? await ResolveLatestFabricAsync(manifest.MinecraftVersion, ct);
                        Report(40, $"安装 Fabric {ver}");
                        await FabricDownloader.InstallAsync(
                            mcFolder, manifest.MinecraftVersion, ver, instanceId,
                            (p, s) => Report(40 + (int)(p * 0.18), s), ct);
                        break;
                    }

                case ModLoaderKind.Quilt:
                    {
                        Report(40, "安装 Quilt");
                        // 自实现（见 LoaderInstaller.InstallQuiltAsync 的注释）：
                        // 库的 QuiltInstaller 会因 Quilt profile 的库声明没有 sha1
                        // 而在 `Sha1.Value` 上抛 InvalidOperationException，100% 装不上。
                        await LoaderInstaller.InstallQuiltAsync(
                            mcFolder, manifest.MinecraftVersion, manifest.LoaderVersion!, instanceId,
                            (p, s) => Report(40 + (int)(p * 0.18), s), ct);
                        break;
                    }

                case ModLoaderKind.Forge:
                case ModLoaderKind.NeoForge:
                    {
                        var neoForge = manifest.Loader == ModLoaderKind.NeoForge;
                        var loaderName2 = neoForge ? "NeoForge" : "Forge";
                        Report(40, $"安装 {loaderName2}");

                        // Forge / NeoForge 的安装器要跑 java -jar，必须有可用 Java。
                        var java = await FindJavaAsync(loaderName2);
                        await LoaderInstaller.InstallForgeLikeAsync(
                            mcFolder, manifest.MinecraftVersion, manifest.LoaderVersion!, instanceId,
                            neoForge, java,
                            (p, s) => Report(40 + (int)(p * 0.18), s), ct);
                        break;
                    }

                default:
                    warnings.Add($"暂不支持自动安装 {loaderName} 加载器，已只安装原版，请手动补齐");
                    break;
            }
        }
        else
        {
            // 原版整合包：基础版本已经装好了，但要保证实例 id 存在（否则实例列表里看不到）。
            // 原版实例就是 Minecraft 版本本身；若清单里的实例名与 mc 版本不同，复制一份版本目录。
            //
            // 加载器认不出来的包也落在这里：只建实例、不装加载器，
            // 让用户看到「实例有了，但加载器要自己补」比装错加载器（模组全不认）好排查。
            if (!string.Equals(instanceId, manifest.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
                DuplicateVersion(mcFolder, manifest.MinecraftVersion, instanceId);

            if (manifest.Loader == ModLoaderKind.Unknown)
            {
                warnings.Add(
                    $"这个整合包的加载器（{manifest.LoaderVersion}）没能识别出来，已只安装原版 Minecraft "
                    + $"并建好实例「{instanceId}」。请自行补装加载器，否则包里的模组不会生效");
                Serilog.Log.Warning("整合包加载器无法识别，已跳过加载器安装：{Loader}", manifest.LoaderVersion);
            }
        }

        ct.ThrowIfCancellationRequested();

        // ---------- 4. 下载清单里的文件 ----------
        var downloaded = 0;
        var failures = new ConcurrentBag<string>();
        if (manifest.Files.Count > 0)
        {
            phase = ModpackPhase.DownloadFiles;
            Report(60, $"下载整合包文件（0/{filesTotal}）");

            await Parallel.ForEachAsync(
                manifest.Files,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(SettingsService.Current.DownloadThreads, 1, 4),
                    CancellationToken = ct,
                },
                async (file, token) =>
                {
                    string target;
                    try
                    {
                        target = ResolveInside(gameRoot, file.Path);
                    }
                    catch (Exception ex)
                    {
                        // 路径非法（含 .. 上跳 / UNC / 绝对路径）—— 记成失败并让详情页亮红灯
                        var reason = $"路径非法：{ex.Message}";
                        failures.Add($"{file.Path}（{reason}）");
                        Interlocked.Increment(ref filesDone);
                        Report(PercentOfFiles(), $"下载整合包文件（{Volatile.Read(ref filesDone)}/{filesTotal}）",
                            failedFile: file.Path, failedReason: reason);
                        return;
                    }

                    try
                    {
                        // 先告诉详情页「开始下这个文件了」，用户才知道卡在哪个文件上
                        Report(PercentOfFiles(), $"正在下载 {Path.GetFileName(file.Path)}",
                            startedFile: file.Path);

                        if (!(File.Exists(target) && file.Size > 0 && new FileInfo(target).Length == file.Size))
                        {
                            var dir = Path.GetDirectoryName(target);
                            if (!string.IsNullOrEmpty(dir))
                                Directory.CreateDirectory(dir);
                            await DownloadFileAsync(file.Url, target, FileTimeout, token);
                        }

                        Interlocked.Increment(ref filesDone);
                        Interlocked.Increment(ref downloaded);
                        Report(PercentOfFiles(), $"下载整合包文件（{Volatile.Read(ref filesDone)}/{filesTotal}）",
                            completedFile: file.Path);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "整合包文件下载失败：{Path}", file.Path);
                        var reason = Describe(ex);
                        failures.Add($"{Path.GetFileName(file.Path)}（{reason}）");
                        Interlocked.Increment(ref filesDone);
                        Report(PercentOfFiles(), $"下载整合包文件（{Volatile.Read(ref filesDone)}/{filesTotal}）",
                            failedFile: file.Path, failedReason: reason);
                    }
                });
        }

        if (failures.Count > 0)
            foreach (var f in failures.Take(10))
                skipped.Add(f);

        ct.ThrowIfCancellationRequested();

        // ---------- 5. 铺 overrides ----------
        var overridden = 0;
        if (manifest.OverrideFolders.Count > 0)
        {
            phase = ModpackPhase.Overrides;
            Report(92, "写入整合包自带文件");
            foreach (var folder in manifest.OverrideFolders)
                overridden += await ExtractOverridesAsync(filePath, folder, gameRoot, ct);
        }

        // ---------- 6. 收尾 ----------
        phase = ModpackPhase.Verifying;
        Report(98, "校验实例");

        // 确保版本 JSON 是规范的（老版本补 arguments、修时区），否则启动会崩。
        try
        {
            VersionJsonNormalizer.NormalizeFile(
                Path.Combine(mcFolder, "versions", instanceId, instanceId + ".json"));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "整合包实例版本 JSON 规范化失败：{Id}", instanceId);
        }

        // 用库解析一遍，确认这个实例真的能被识别（避免装完却在列表里看不到 / 启动不了）。
        try
        {
            var parsed = new MinecraftParser(mcFolder).GetMinecraft(instanceId);
            if (parsed is null)
                warnings.Add($"实例 {instanceId} 安装完成但解析失败，可能无法启动");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "整合包实例解析失败：{Id}", instanceId);
            warnings.Add($"实例 {instanceId} 解析异常：{ex.Message}");
        }

        phase = ModpackPhase.Done;
        Report(100, "安装完成");

        Serilog.Log.Information(
            "整合包安装完成：{Name} → {Id}（下载 {Dl} 个，覆盖 {Ov} 个，跳过 {Skip} 个）",
            manifest.Name, instanceId, downloaded, overridden, skipped.Count);

        return new ModpackInstallResult
        {
            InstanceId = instanceId,
            DownloadedFiles = downloaded,
            OverriddenFiles = overridden,
            Skipped = skipped,
            Warnings = warnings,
        };
    }

    // ==================== 基础版本 ====================

    private static async Task EnsureVanillaAsync(
        string mcFolder, string mcVersion, CancellationToken ct, Action<int> onPercent)
    {
        ct.ThrowIfCancellationRequested();

        // 已经装过就直接用，不重复下载（整合包只关心「有这个基础版本」）。
        //
        // ⚠️ `MinecraftParser.GetMinecraft()` 在**实例目录不存在时会抛 DirectoryNotFoundException**，
        // 而不是像名字暗示的那样返回 null（实测：
        // `versions\1.20.1 not found`）。不接住的话，「装一个本地还没有的版本」这一步会
        // 直接把整个安装带崩 —— 1.0.6.5 起一直如此，只是之前测的都是本地已装的版本才没暴露。
        // 这里把「解析不出来」和「解析抛异常」统一视作**还没装**，继续走下面的下载流程。
        var installed = false;
        try
        {
            installed = new MinecraftParser(mcFolder).GetMinecraft(mcVersion) is not null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "整合包安装：基础版本 {Version} 尚未安装（或版本 JSON 读不出）", mcVersion);
        }

        if (installed)
        {
            onPercent(100);
            return;
        }

        var manifestEntry = await GetVersionEntryAsync(mcVersion, ct);

        // ⚠️ ct 必须传下去：这一步是几百 MB 的原版下载，不传令牌的话
        // 用户在进度页点「取消安装」只会看到「正在取消…」一直转，实际照样下完。
        await VanillaDownloader.InstallAsync(mcFolder, manifestEntry, (p, _, _) => onPercent(p), ct);
    }

    /// <summary>从版本清单里找一条记录（带官方 URL）。清单拉不到时用 BMCLAPI 的地址兜底。</summary>
    private static async Task<VersionManifestEntry> GetVersionEntryAsync(string mcVersion, CancellationToken ct)
    {
        List<VersionManifestEntry>? all = null;
        try
        {
            all = await MinecraftVersionCache.GetAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "整合包安装：版本清单拉取失败，改用镜像地址兜底");
        }

        var entry = all?.FirstOrDefault(v => string.Equals(v.Id, mcVersion, StringComparison.OrdinalIgnoreCase));
        if (entry is not null && !string.IsNullOrWhiteSpace(entry.Url))
            return entry;

        // 清单里没有（或没带 URL）时，用镜像地址兜底；VanillaDownloader 自己还有双源重试。
        return new VersionManifestEntry
        {
            Id = mcVersion,
            Url = DownloadSources.VersionJsonMirror(mcVersion),
            Type = "release",
        };
    }

    private static async Task<string> ResolveLatestFabricAsync(string mcVersion, CancellationToken ct)
    {
        var list = await FabricDownloader.EnumerableFabricAsync(mcVersion, null, ct);
        var stable = list.FirstOrDefault(x => x.Stable) ?? list.FirstOrDefault();
        if (stable is null)
            throw new ModpackException($"找不到 Minecraft {mcVersion} 可用的 Fabric 加载器版本");
        return stable.Version;
    }

    /// <summary>
    /// 取一个可用的 Java 路径；找不到就抛出**可操作**的错误（告诉用户去哪儿配）。
    /// <para>
    /// 不直接把 <c>JavaHelper.FindFirstJavaPathAsync()</c> 的结果塞进 <c>??</c> ——
    /// 它内部会枚举候选并逐个起进程探测，个别损坏的 Java 会让它抛异常而非返回 null。
    /// 这里统一收口，避免一个「探测失败」被冒泡成一堆看不懂的栈。
    /// </para>
    /// </summary>
    private static async Task<string> FindJavaAsync(string forWhat)
    {
        string? path = null;
        try
        {
            path = await JavaHelper.FindFirstJavaPathAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "查找 Java 失败（{Loader}）", forWhat);
        }

        return path ?? throw new ModpackException(
            $"安装 {forWhat} 需要 Java，但没有找到可用的 Java 运行环境 —— "
            + "请到「设置 → 启动」配置 Java 路径后重试");
    }


    // ==================== 覆盖层解包 ====================

    /// <summary>把整合包里的某个覆盖层目录铺到实例根目录，返回写入的文件数。</summary>
    private static async Task<int> ExtractOverridesAsync(
        string filePath, string folderName, string instanceRoot, CancellationToken ct)
    {
        var count = 0;

        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            var prefix = folderName.TrimEnd('/') + "/";

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();

                // 只处理该覆盖层下的**文件**（目录条目以 / 结尾）
                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.Length == 0 && entry.Name.Length == 0)
                    continue;
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var relative = entry.FullName[prefix.Length..];
                string target;
                try
                {
                    target = ResolveInside(instanceRoot, relative);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "整合包覆盖层条目路径非法，已跳过：{Entry}", entry.FullName);
                    continue;
                }

                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                await using (var src = entry.Open())
                await using (var dst = new FileStream(
                    target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await src.CopyToAsync(dst, ct);
                }

                count++;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "解包整合包覆盖层失败：{Folder}", folderName);
        }

        return count;
    }

    // ==================== 实例 id / 目录 ====================

    /// <summary>
    /// 整合包资源的落点根目录 —— 必须与启动时的 gameDir 同源。
    /// <para>
    /// 判定条件与 <c>RunPageViewModel</c> 里 <c>cfg.VersionIsolation ? versions/&lt;id&gt; : mcFolder</c>
    /// 完全一致。**改一处必须改另一处**，不然就会出现「下进去了但游戏读不到」。
    /// </para>
    /// </summary>
    private static string ResolveGameRoot(string mcFolder, string instanceId)
    {
        // ⚠️ 这里**绝不** Directory.CreateDirectory！
        //
        // 版本隔离开着时，gameRoot 就是 versions/<instanceId> —— 也就是「待安装的实例目录」。
        // 一旦提前把它建出来，它就成了 versions/ 下一个**没有版本 JSON 的空目录**，
        // 而库的 `MinecraftParser.GetMinecrafts()` 会遍历 versions/ 下**每一个**子目录并解析，
        // 撞上这种空目录直接抛 `FileNotFoundException("client.json not found in ...")`
        // （MinecraftParser.Parse 第 112 行）—— 而 Quilt / Forge / NeoForge 三个安装器
        // 内部都要调 GetMinecrafts() 去找 parent，于是**必然装不上**。
        //
        // 目录的创建推迟到真正要往里写文件的地方（DownloadFilesAsync / 覆盖层铺开），
        // 那时实例版本 JSON 已经由加载器安装器写好了，空目录窗口期不存在。
        return SettingsService.Current.VersionIsolation && !string.IsNullOrWhiteSpace(instanceId)
            ? Path.Combine(mcFolder, "versions", instanceId)
            : mcFolder;
    }

    /// <summary>
    /// 清掉 versions/ 下**没有版本 JSON 的空壳目录**。
    /// <para>
    /// 库的解析器对这类目录是零容忍的（见 <see cref="ResolveGameRoot"/> 注释），
    /// 只要存在一个，所有走库安装器的加载器都装不上。历史失败残留、
    /// 或用户手动建的半截目录都会踩到，所以每次装加载器前先扫一遍。
    /// </para>
    /// <para>只删「空目录」或「目录里没有 &lt;目录名&gt;.json」的目录，绝不碰有真实版本的目录。</para>
    /// </summary>
    private static void PurgeBrokenInstanceDirs(string mcFolder)
    {
        var versionsDir = Path.Combine(mcFolder, "versions");
        if (!Directory.Exists(versionsDir)) return;

        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            var name = Path.GetFileName(dir);
            if (File.Exists(Path.Combine(dir, name + ".json"))) continue;

            // 有版本 JSON 的目录一律不动；没有 JSON 的才可能是「空壳」。
            // 但目录里若还有真实内容（比如用户放了 mods），也不能乱删 —— 只清明确没用的。
            var hasAnyFile = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();
            if (hasAnyFile) continue;

            try
            {
                Directory.Delete(dir, recursive: true);
                Serilog.Log.Information("整合包安装：清理无版本 JSON 的空壳实例目录 {Dir}", dir);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "清理空壳实例目录失败（忽略）：{Dir}", dir);
            }
        }
    }

    private static string BuildInstanceId(string mcFolder, ModpackManifest manifest)
    {
        // 「名字_加载器_版本」的组合，例如 MyPack_fabric_0.15.11
        var loaderPart = manifest.Loader == ModLoaderKind.Vanilla
            ? string.Empty
            : "_" + manifest.Loader.ToString().ToLowerInvariant();
        var verPart = manifest.Loader is ModLoaderKind.Vanilla or ModLoaderKind.Unknown || string.IsNullOrWhiteSpace(manifest.LoaderVersion)
            ? string.Empty
            : "_" + manifest.LoaderVersion;

        var baseName = SanitizeName(manifest.Name);
        if (string.IsNullOrEmpty(baseName))
            baseName = "Modpack";

        var candidate = baseName + loaderPart + verPart;
        var versionsDir = Path.Combine(mcFolder, "versions");

        // 重名就加 (1) (2)…，绝不覆盖已有实例。
        //
        // ⚠️ 判据是「有没有版本 JSON」而不是「目录在不在」，口径与 `GameInstanceHelper.GetInstanceIds()`
        // 一致（它也是扫 `versions/*/<id>.json`）。只判目录的话，一次**失败**的安装会留下
        // 一个空目录（`ResolveGameRoot` 建的），下次重试就被当成重名 → 用户每试一次多一个 (1)(2)(3)。
        if (!IsInstanceIdTaken(versionsDir, candidate))
            return candidate;

        for (var i = 1; i < 1000; i++)
        {
            var next = $"{candidate} ({i})";
            if (!IsInstanceIdTaken(versionsDir, next))
                return next;
        }

        return $"{candidate}_{Guid.NewGuid():N}";
    }

    /// <summary>
    /// 这个实例 id 是否已被「有效实例」占用。
    /// <para>
    /// 判据与 <c>GameInstanceHelper.GetInstanceIds()</c> 保持一致：`versions/&lt;id&gt;/&lt;id&gt;.json`
    /// 存在才算占用。**只看目录会被失败残留骗到** —— 失败的安装已经由
    /// <see cref="ResolveGameRoot"/> 建出了 `versions/&lt;id&gt;`，但里面没有版本 JSON。
    /// </para>
    /// </summary>
    private static bool IsInstanceIdTaken(string versionsDir, string instanceId)
        => File.Exists(Path.Combine(versionsDir, instanceId, instanceId + ".json"));

    /// <summary>把名字清洗成安全的目录名（去掉非法字符、压缩空白、限制长度）。</summary>
    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim().TrimEnd('.');

        // 去掉可能被当成路径分隔的斜杠残留
        cleaned = cleaned.Replace('/', '_').Replace('\\', '_');

        return cleaned.Length > 60 ? cleaned[..60].TrimEnd('.', ' ') : cleaned;
    }

    /// <summary>
    /// 原版整合包：复制一份版本目录，让实例名与 mc 版本不同也能在列表里出现。
    /// <para>
    /// ⚠️ 必须**幂等**：调用前 <see cref="ResolveGameRoot"/> 可能已经建出
    /// <c>versions/&lt;id&gt;</c>（放 mods / config），所以不能拿「目标目录存在」当跳过条件
    /// ——那样会把版本 JSON 漏掉，实例在列表里认不出来。改为逐文件补齐缺失项。
    /// </para>
    /// </summary>
    private static void DuplicateVersion(string mcFolder, string sourceId, string targetId)
    {
        try
        {
            var srcDir = Path.Combine(mcFolder, "versions", sourceId);
            var dstDir = Path.Combine(mcFolder, "versions", targetId);
            if (!Directory.Exists(srcDir))
                return;

            Directory.CreateDirectory(dstDir);

            foreach (var file in Directory.GetFiles(srcDir))
            {
                var dst = Path.Combine(dstDir, Path.GetFileName(file));
                if (File.Exists(dst))
                    continue;
                File.Copy(file, dst, overwrite: false);
            }

            // 版本 JSON 内部 id 要跟着改，否则解析器认的是旧 id
            var jsonPath = Path.Combine(dstDir, targetId + ".json");
            var oldJson = Path.Combine(dstDir, sourceId + ".json");
            if (File.Exists(oldJson) && !File.Exists(jsonPath))
            {
                var text = File.ReadAllText(oldJson);
                text = Regex.Replace(text, @"""id""\s*:\s*""[^""]*""", $"\"id\": \"{targetId}\"");
                File.WriteAllText(jsonPath, text);
                File.Delete(oldJson);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "复制原版版本目录失败：{Src} → {Dst}", sourceId, targetId);
        }
    }

    // ==================== 路径安全 / 下载 ====================

    /// <summary>
    /// 把相对路径解析到实例目录内，并**确认没有逃出去**（防目录穿越）。
    /// 整合包是外部输入，<c>path</c> 可能是 <c>../../../../Windows/System32/xxx</c> 或绝对路径。
    /// </summary>
    public static string ResolveInside(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("路径为空", nameof(relativePath));

        // UNC 路径（\\server\share\...）必须直接拒绝：把反斜杠统一成斜杠后它会退化成
        // 一个看似相对的 "server/share/..."，进而被悄悄重写成实例内的目录 —— 虽然落点仍在
        // 实例内、不会真的写到网络共享上，但这是对包作者的意图的曲解，直接判非法更安全。
        if (relativePath.StartsWith("\\\\", StringComparison.Ordinal)
            || relativePath.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("是 UNC 路径");

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');

        // 明确的拒绝项（比 Path.GetFullPath 更直接，也更能给出准确原因）
        if (normalized.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("包含 .. 上跳");
        if (normalized.Length >= 2 && normalized[1] == ':')
            throw new InvalidOperationException("是绝对路径");

        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, normalized));

        // 最终校验：解析后的路径必须仍在 root 之下（防符号链接等边缘情况）
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("路径逃出了实例目录");
        }

        return full;
    }

    /// <summary>
    /// 下载整合包里的一个文件。
    ///
    /// 1.0.7.15 起改为转发给 <see cref="FileDownloader"/>：分块并行 + 断点续传 + 体积/sha1 校验，
    /// 比原来「单连接 CopyToAsync + 30 秒整体超时」快得多，也不会在慢速链路上把大文件误判成超时。
    /// <paramref name="url"/> 的镜像改写、换源、重试都在内核里统一处理。
    /// </summary>
    private static async Task DownloadFileAsync(
        string url, string targetPath, TimeSpan timeout, CancellationToken ct)
    {
        var item = new DownloadItem(
            url,
            targetPath,
            DisplayName: Path.GetFileName(targetPath));

        await FileDownloader.DownloadAsync(item, onBytes: null, onStatus: null, ct: ct)
            .ConfigureAwait(false);
    }

    /// <summary>每次尝试独立临时名，避免并发下载同名目标时互相锁死（同 FabricDownloader 的做法）。</summary>
    private static string MakeTempPath(string targetPath) => $"{targetPath}.{Guid.NewGuid():N}.vbltmp";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 清理失败不外抛 */ }
    }

    // ==================== 小工具 ====================

    /// <summary>在 ZIP 里按名字找条目，忽略大小写与前后斜杠。</summary>
    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name)
    {
        foreach (var e in archive.Entries)
        {
            var n = e.FullName.Trim('/').Replace('\\', '/');
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    /// <summary>
    /// 判断整合包里有没有某个顶层目录。
    ///
    /// <b>不能只找目录条目</b>：很多打包器（含 Python 的 zipfile）只写文件条目、
    /// 不写 <c>overrides/</c> 这样的空目录条目，此时按条目名找会漏判。
    /// 因此同时接受三种证据：目录条目本身、以 <c>prefix/</c> 开头的任意条目。
    /// </summary>
    private static bool HasFolder(ZipArchive archive, string folder)
    {
        var prefix = folder.Trim('/') + "/";

        foreach (var e in archive.Entries)
        {
            var n = e.FullName.Replace('\\', '/').TrimStart('/');

            if (string.Equals(n.TrimEnd('/'), folder.Trim('/'), StringComparison.OrdinalIgnoreCase))
                return true;                                   // 目录条目本身

            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;                                   // 目录下的文件
        }

        return false;
    }

    private static string? GetString(JsonElement obj, string prop)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(prop, out var el)
           && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static bool TryGetString(JsonElement obj, string prop, out string value)
    {
        value = string.Empty;
        var s = GetString(obj, prop);
        if (string.IsNullOrEmpty(s))
            return false;
        value = s;
        return true;
    }

    /// <summary>
    /// 取 files[].downloads[] 里第一个 http(s) 地址。
    /// <para>
    /// ⚠️ 整合包规范只要求「可直接下载的 URL」，**没有**限定 https。早期这里写死
    /// <c>https://</c> 前缀，导致自建镜像 / 局域网服务（<c>http://</c>）的条目全被当成
    /// 「没有下载地址」跳过。只挡其它协议（<c>file://</c> / <c>ftp://</c> 之类）。
    /// </para>
    /// </summary>
    private static string? FirstDownloadUrl(JsonElement file)
    {
        if (!file.TryGetProperty("downloads", out var dl) || dl.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var u in dl.EnumerateArray())
        {
            if (u.ValueKind != JsonValueKind.String)
                continue;
            var s = u.GetString();
            if (string.IsNullOrWhiteSpace(s))
                continue;
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return s;
        }

        return null;
    }

    /// <summary>把下载/IO 异常翻成一句可读原因。</summary>
    private static string Describe(Exception ex)
    {
        var fs = DownloadErrors.DescribeFileSystem(ex);
        if (fs is not null)
            return fs;

        if (ex is HttpRequestException { StatusCode: not null } h)
            return $"服务器返回 {(int)h.StatusCode!.Value}";

        if (ex is OperationCanceledException or TimeoutException || ex.InnerException is TimeoutException)
            return "超时";

        if (ex is HttpRequestException)
            return "网络错误";

        return ex.Message;
    }
}

/// <summary>整合包安装过程中的可展示异常。</summary>
public class ModpackException : Exception
{
    public ModpackException(string message) : base(message) { }
    public ModpackException(string message, Exception? inner) : base(message, inner) { }
}
