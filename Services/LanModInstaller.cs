using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Services;

/// <summary>已安装的局域网联机模组种类。</summary>
public enum LanModKind
{
    /// <summary>没装。</summary>
    None = 0,

    /// <summary>
    /// LAN World Plug-n-Play（<c>mcwifipnp</c>）—— 本启动器安装的目标模组。
    /// 给「对局域网开放」加上正版验证开关（启用 / 禁用 / 禁用 + 修复 UUID）、端口与 UPnP 映射。
    /// </summary>
    McWifiPnp,

    /// <summary>e4mc —— 旧方案，只用于识别历史实例；一键安装会把它替换掉。</summary>
    E4mc,

    /// <summary>LAN Server Properties —— 旧方案，只用于识别历史实例。</summary>
    LanServerProperties,
}

/// <summary>某个实例当前的联机模组状态。</summary>
public sealed class LanModState
{
    public LanModKind Kind { get; init; } = LanModKind.None;

    /// <summary>模组的展示名（<c>LAN World Plug-n-Play</c> / <c>e4mc</c>）。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>命中的 jar 文件名（给界面显示「已装哪个文件」）。</summary>
    public string FileName { get; init; } = string.Empty;

    public bool IsInstalled => Kind != LanModKind.None;
}

/// <summary>一次「一键安装联机模组」的结果。</summary>
public sealed class LanModInstallResult
{
    public bool Success { get; init; }
    public LanModKind Kind { get; init; } = LanModKind.None;
    public string Title { get; init; } = string.Empty;

    /// <summary>一句话结论；失败时是失败原因。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>装完怎么用（不同模组的联机方式完全不同，必须分开讲）。</summary>
    public string Guide { get; init; } = string.Empty;

    /// <summary>真正落盘的文件（含顺带补上的 Fabric API）。</summary>
    public List<string> Files { get; init; } = new();

    /// <summary>为装这个模组而删掉的同类旧文件（重复安装同名 mod 会让游戏直接报错）。</summary>
    public List<string> Removed { get; init; } = new();
}

/// <summary>一次模组下载的落地情况。</summary>
internal sealed record ModDownloadOutcome(bool Ok, List<string> Removed);

/// <summary>
/// 给某个实例「一键装好局域网联机模组」。
///
/// <para>
/// <b>为什么需要模组</b>：原版「对局域网开放」继承主界面的在线验证，房主一开房就会要求
/// 每个加入者都通过 Mojang 验证 —— 离线账户（启动器里用离线登录的玩家）会被直接踢掉，
/// 这也是 EasyTier 组网成功后仍然连不上的原因。要绕过它只有装模组这一条路。
/// </para>
///
/// <para>
/// <b>装的是什么</b>：<see href="https://modrinth.com/mod/mcwifipnp">LAN World Plug-n-Play</see>
/// （slug <c>mcwifipnp</c>）。它是唯一一个在 Fabric / Quilt / Forge / NeoForge 四家、
/// 并且从 1.15 一路覆盖到最新快照的联机模组，功能也最贴合离线联机：
/// <list type="bullet">
/// <item>「对局域网开放」界面多出「正版验证」按钮，三档：启用 / 禁用 / <b>禁用 + 修复 UUID</b>
/// （最后一档会用玩家名去比对上 Mojang 的 UUID，切换模式时背包不会丢）。</item>
/// <item>端口可改，并且可以走 UPnP 自动映射到公网（路由器支持的话，不打洞也能直连）。</item>
/// <item>顺带提供 PVP 开关、MOTD、加入即 OP、白名单、封禁等服务器级设置。</item>
/// </list>
/// 查不到适配版本时就如实告诉用户「这个版本暂无适配」，而不是随便塞一个版本进去让游戏崩在启动阶段。
/// </para>
///
/// <para>
/// <b>Fabric API</b>：mcwifipnp 的 Fabric 构建把 Fabric API 标成了必需依赖，缺了会加载失败。
/// 这里在装主模组之前顺手检查一遍，缺就一起装上 —— 否则用户看到的是「装好了但进游戏没反应」。
/// </para>
/// </summary>
public static class LanModInstaller
{
    /// <summary>LAN World Plug-n-Play 的 Modrinth slug。</summary>
    public const string McWifiPnpSlug = "mcwifipnp";

    /// <summary>e4mc 的 Modrinth slug（旧方案，只用于识别与清理）。</summary>
    public const string E4mcSlug = "e4mc";

    /// <summary>LAN Server Properties 的 Modrinth slug（旧方案，只用于识别与清理）。</summary>
    public const string LanServerPropertiesSlug = "lan-server-properties";

    /// <summary>Fabric API 的 Modrinth slug。</summary>
    public const string FabricApiSlug = "fabric-api";

    /// <summary>一个候选模组。</summary>
    private sealed class Candidate
    {
        public string Slug { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public LanModKind Kind { get; init; }
        public ModLoaderKind[] Loaders { get; init; } = Array.Empty<ModLoaderKind>();
        public string Guide { get; init; } = string.Empty;

        /// <summary>是否需要（对 Fabric 系实例）补 Fabric API。</summary>
        public bool NeedsFabricApi { get; init; }
    }

    /// <summary>
    /// 候选顺序 = 优先级。这里只留 LAN World Plug-n-Play 一个：
    /// 它四家加载器全覆盖（1.15 ~ 26.4 快照），不再需要 e4mc / LAN Server Properties 兜底，
    /// 少一个候选也少一次「装完发现两个模组抢同一个界面」的麻烦。
    /// </summary>
    private static readonly Candidate[] Candidates =
    {
        new()
        {
            Slug = McWifiPnpSlug,
            Title = "LAN World Plug-n-Play",
            Kind = LanModKind.McWifiPnp,
            Loaders = new[]
            {
                ModLoaderKind.Fabric, ModLoaderKind.Quilt, ModLoaderKind.Forge, ModLoaderKind.NeoForge,
            },
            NeedsFabricApi = true,
            Guide = "进游戏 → Esc → 对局域网开放。新出现的「正版验证」按钮选「禁用 + 修复 UUID」"
                  + "（离线玩家能进，同时尽量保留老玩家的背包和 UUID）；端口可以自己改。"
                  + "朋友用你的 EasyTier 虚拟 IP + 这个端口连接；路由器支持 UPnP 的话，"
                  + "在同一个界面把 UPnP 打开就能走公网直连。",
        },
    };

    // ==================== 状态检测 ====================

    /// <summary>
    /// 看某个实例的 <c>mods/</c> 里有没有联机模组。
    /// 只按文件名认 —— 这几个模组的文件名是固定的（<c>mcwifipnp-2.1.4-26.3-forge.jar</c> /
    /// <c>e4mc-fabric-6.2.3.jar</c>），比解压读元数据快得多，也不会被坏 jar 卡住。
    /// </summary>
    public static LanModState Detect(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return new LanModState();

        try
        {
            var modsFolder = GameInstanceHelper.ResolveInstanceFolder(instanceId, "mods");
            if (!Directory.Exists(modsFolder))
                return new LanModState();

            // 目标模组优先：万一 mods 里同时躺着新旧两个（用户手动装过 e4mc），
            // 界面应该报告「已装 LAN World Plug-n-Play」，而不是被遍历顺序左右。
            string? fallbackFile = null;
            LanModKind fallbackKind = LanModKind.None;

            foreach (var jar in Directory.GetFiles(modsFolder, "*.jar"))
            {
                var name = Path.GetFileName(jar);
                var kind = ClassifyFileName(name);

                if (kind == LanModKind.McWifiPnp)
                    return new LanModState
                    {
                        Kind = kind,
                        DisplayName = DisplayNameOf(kind),
                        FileName = name,
                    };

                if (kind != LanModKind.None && fallbackFile is null)
                {
                    fallbackFile = name;
                    fallbackKind = kind;
                }
            }

            if (fallbackFile is not null)
                return new LanModState
                {
                    Kind = fallbackKind,
                    DisplayName = DisplayNameOf(fallbackKind),
                    FileName = fallbackFile,
                };
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "检测联机模组失败：{Instance}", instanceId);
        }

        return new LanModState();
    }

    /// <summary>文件名 → 模组种类。</summary>
    private static LanModKind ClassifyFileName(string fileName)
    {
        var n = fileName.ToLowerInvariant();

        // mcwifipnp-2.1.4-26.3-forge.jar / mcwifipnp-2.0.8-1.21.9-neoforge.jar
        if (n.StartsWith("mcwifipnp", StringComparison.Ordinal))
            return LanModKind.McWifiPnp;

        // Fabric 版的 e4mc jar 名是 e4mc-fabric-…，Forge / NeoForge 是 e4mc-forge-… / e4mc-neoforge-…，
        // 但「-modern」变体也一样以 e4mc 开头，所以统一按前缀认。
        if (n.StartsWith("e4mc", StringComparison.Ordinal))
            return LanModKind.E4mc;

        if (n.Contains("lanserverproperties", StringComparison.Ordinal))
            return LanModKind.LanServerProperties;

        return LanModKind.None;
    }

    private static string DisplayNameOf(LanModKind kind) => kind switch
    {
        LanModKind.McWifiPnp => "LAN World Plug-n-Play",
        LanModKind.E4mc => "e4mc",
        LanModKind.LanServerProperties => "LAN Server Properties",
        _ => "未安装",
    };

    // ==================== 安装 ====================

    /// <summary>
    /// 给实例装联机模组。
    ///
    /// <para>
    /// 全程不抛异常（除 <see cref="OperationCanceledException"/>）：所有失败都转成
    /// <see cref="LanModInstallResult.Message"/> 交给界面 —— 这个方法直接从命令里 await，
    /// 漏一个异常出去就是整个启动器崩掉。
    /// </para>
    /// </summary>
    /// <param name="instance">目标实例（含已识别的加载器）。</param>
    /// <param name="onStatus">阶段提示，例如「正在查询 LAN World Plug-n-Play 的可用版本…」。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<LanModInstallResult> InstallAsync(
        GameInstance instance,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        if (instance is null)
            return Fail("没有选择实例");

        // ① 加载器门槛：原版实例装不了模组，未识别说明版本 JSON 有问题
        if (instance.Loader == ModLoaderKind.Vanilla)
        {
            return Fail($"{instance.Id} 是原版实例，没有安装任何模组加载器。\n"
                        + "请先在「下载」页给这个版本装上 Fabric / Forge / NeoForge，再回来点这个按钮。");
        }

        if (instance.Loader == ModLoaderKind.Unknown)
        {
            return Fail($"{instance.Id} 的版本 JSON 读不出来，无法判断加载器。\n"
                        + "建议先用「版本管理」里的体检/修复功能检查一下这个实例。");
        }

        if (instance.Loader == ModLoaderKind.LiteLoader)
            return Fail("LiteLoader 没有可用的局域网联机模组，请改用 Fabric 或 Forge 实例。");

        var loaderName = ModLoaderHelper.DisplayNameOf(instance.Loader);
        var tag = LoaderTag(instance.Loader);
        if (tag is null)
            return Fail($"暂不支持 {loaderName} 实例。");

        // ② 原版版本号：加载器实例的版本号写在父版本里，必须沿 inheritsFrom 走到底
        var mcFolder = GameInstanceHelper.GetMinecraftFolder();
        var gameVersion = InstanceIntegrityChecker.ResolveRootVersion(mcFolder, instance.Id);
        if (string.IsNullOrWhiteSpace(gameVersion))
            return Fail($"读不出 {instance.Id} 对应的 Minecraft 版本号。");

        string modsFolder;
        try
        {
            modsFolder = GameInstanceHelper.ResolveInstanceFolder(instance.Id, "mods");
            Directory.CreateDirectory(modsFolder);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "创建 mods 目录失败：{Folder}", instance.Id);
            return Fail($"无法创建模组目录：{ex.Message}");
        }

        // ③ 按优先级找候选：第一个「真的有适配本加载器 + 本版本」的候选胜出
        var tried = new List<string>();
        foreach (var candidate in Candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!candidate.Loaders.Contains(instance.Loader))
                continue;

            onStatus?.Invoke($"正在查询 {candidate.Title}（{loaderName} / MC {gameVersion}）…");

            var file = await FindCompatibleFileAsync(candidate.Slug, tag, gameVersion, ct);
            if (file is null)
            {
                tried.Add(candidate.Title);
                Serilog.Log.Information(
                    "联机模组：{Slug} 没有适配 {Loader} / MC {Version} 的版本", candidate.Slug, loaderName, gameVersion);
                continue;
            }

            // ④ Fabric 系先补 Fabric API
            var installed = new List<string>();
            if (candidate.NeedsFabricApi && instance.Loader is ModLoaderKind.Fabric or ModLoaderKind.Quilt)
            {
                var apiFile = await EnsureFabricApiAsync(modsFolder, gameVersion, candidate.Title, onStatus, installed, ct);
                if (!apiFile)
                {
                    // Fabric API 装不上（多半是这个版本真没发）→ 不要硬装主模组，
                    // 否则用户会得到一个「装了但加载失败」的实例
                    return Fail($"装 {candidate.Title} 需要 Fabric API，"
                                + $"但在 MC {gameVersion} 上没找到可用的 Fabric API 版本。");
                }
            }

            onStatus?.Invoke($"正在下载 {candidate.Title}…");

            var outcome = await DownloadModAsync(file, modsFolder, candidate.Slug, candidate.Kind, onStatus, ct);
            if (!outcome.Ok)
                return Fail($"下载 {candidate.Title} 失败，请检查网络后重试。");

            var kind = ClassifyFileName(file.FileName);
            if (kind == LanModKind.None)
                kind = candidate.Kind;

            var message = $"已安装到 {GameInstanceHelper.DescribeInstanceFolder(instance.Id, "mods")}/{file.FileName}";
            if (outcome.Removed.Count > 0)
            {
                message += "\n同时清理了同类的旧联机模组：" + string.Join("、", outcome.Removed)
                           + "（它们都接管「对局域网开放」，装两份会互相抢界面）。";
            }

            // Files 要给出「这次真正装进去的全部文件」：主模组 + 顺带补的 Fabric API。
            // 以前只有 Fabric API 时会把主模组漏掉（条件表达式的两侧互斥）。
            var files = new List<string>(installed) { file.FileName };

            return new LanModInstallResult
            {
                Success = true,
                Kind = kind,
                Title = candidate.Title,
                Message = message,
                Guide = candidate.Guide,
                Files = files,
                Removed = outcome.Removed,
            };
        }

        // ⑤ 全都查不到 —— 如实说明，别让用户以为是网络问题反复点
        var hint = tried.Count == 0
            ? $"没有适配 {loaderName} 的局域网联机模组"
            : $"{string.Join(" / ", tried)} 都没有适配 MC {gameVersion} 的版本";

        return Fail($"{hint}。\n"
                    + "可以换一个加载器（Fabric 通常支持得最全）或更新的游戏版本试试，"
                    + "也可以到 Modrinth 上手动挑一个联机模组放进 mods 目录。");
    }

    /// <summary>
    /// 在 Modrinth 上找某个项目「适配指定加载器 + 游戏版本」的最新文件。
    /// 两个筛选都交给 API，拿到结果后再本地复核一遍 <c>game_versions</c>，
    /// 防止 API 的模糊匹配放过一个只写了大版本的条目。
    /// </summary>
    private static async Task<ModrinthApi.FileInfo?> FindCompatibleFileAsync(
        string slug, string loaderTag, string gameVersion, CancellationToken ct)
    {
        try
        {
            var versions = await ModrinthApi.GetProjectVersionsAsync(slug, loaderTag, gameVersion, ct);

            var hit = versions.FirstOrDefault(v =>
                v.PrimaryFile is { Url.Length: > 0 }
                && v.GameVersions.Any(g => string.Equals(g, gameVersion, StringComparison.OrdinalIgnoreCase)));

            // 退一步：API 没按 game_versions 精确命中时（快照版本号写法差异），
            // 只要加载器对得上、且版本列表里含这个游戏版本，就认为可用。
            if (hit is null)
            {
                hit = versions.FirstOrDefault(v =>
                    v.PrimaryFile is { Url.Length: > 0 }
                    && v.Loaders.Contains(loaderTag, StringComparer.OrdinalIgnoreCase));
            }

            return hit?.PrimaryFile;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "查询联机模组版本失败：{Slug}", slug);
            return null;
        }
    }

    /// <summary>
    /// 确保 mods 目录里有一份 Fabric API。
    /// 返回 true 表示「已经有了 / 这次装好了」，false 表示装不上。
    /// </summary>
    private static async Task<bool> EnsureFabricApiAsync(
        string modsFolder,
        string gameVersion,
        string hostTitle,
        Action<string>? onStatus,
        List<string> installed,
        CancellationToken ct)
    {
        if (HasFabricApi(modsFolder))
            return true;

        onStatus?.Invoke($"正在补装 Fabric API（{hostTitle} 的必需依赖）…");

        var file = await FindCompatibleFileAsync(FabricApiSlug, "fabric", gameVersion, ct);
        if (file is null)
            return false;

        var outcome = await DownloadModAsync(file, modsFolder, FabricApiSlug, LanModKind.None, onStatus, ct);
        if (outcome.Ok)
            installed.Add(file.FileName);

        return outcome.Ok;
    }

    /// <summary>mods 里是否已有 Fabric API（文件名口径，与其它地方保持一致）。</summary>
    private static bool HasFabricApi(string modsFolder)
    {
        try
        {
            if (!Directory.Exists(modsFolder))
                return false;

            return Directory.GetFiles(modsFolder, "*.jar").Any(f =>
            {
                var n = Path.GetFileName(f).ToLowerInvariant();
                return n.Contains("fabric-api", StringComparison.Ordinal)
                       || n.Contains("fabric_api", StringComparison.Ordinal)
                       || n.StartsWith("fabricapi", StringComparison.Ordinal);
            });
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 下载一个模组文件到 mods 目录。
    ///
    /// <para>
    /// 落盘前先把这个模组的**旧版本**删掉：同一个模组装两份会被游戏当成重复 mod 直接报错，
    /// 而 Modrinth 的文件名带版本号，不清理就会越装越多。
    /// </para>
    /// </summary>
    private static async Task<ModDownloadOutcome> DownloadModAsync(
        ModrinthApi.FileInfo file,
        string modsFolder,
        string slug,
        LanModKind kind,
        Action<string>? onStatus,
        CancellationToken ct)
    {
        try
        {
            var fileName = string.IsNullOrWhiteSpace(file.FileName) ? $"{slug}.jar" : file.FileName;
            var target = Path.Combine(modsFolder, fileName);

            var removed = RemoveOldCopies(modsFolder, slug, kind, target);

            var item = new DownloadItem(
                file.Url,
                target,
                ExpectedSize: file.Size,
                Sha1: string.IsNullOrWhiteSpace(file.Sha1) ? null : file.Sha1,
                DisplayName: fileName);

            await FileDownloader.DownloadAsync(item, onStatus: onStatus, ct: ct).ConfigureAwait(false);
            return new ModDownloadOutcome(true, removed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "下载联机模组失败：{Slug} → {Folder}", slug, modsFolder);
            return new ModDownloadOutcome(false, new List<string>());
        }
    }

    /// <summary>删掉同一个模组的旧 jar（保留这次要下载的那个文件名），返回被删掉的文件名。</summary>
    private static List<string> RemoveOldCopies(string modsFolder, string slug, LanModKind kind, string keepPath)
    {
        var removed = new List<string>();

        try
        {
            foreach (var jar in Directory.GetFiles(modsFolder, "*.jar"))
            {
                if (string.Equals(jar, keepPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Path.GetFileName(jar).ToLowerInvariant();

                var isSame = slug switch
                {
                    FabricApiSlug => name.Contains("fabric-api", StringComparison.Ordinal)
                                     || name.Contains("fabric_api", StringComparison.Ordinal)
                                     || name.StartsWith("fabricapi", StringComparison.Ordinal),
                    McWifiPnpSlug => name.StartsWith("mcwifipnp", StringComparison.Ordinal),
                    E4mcSlug => name.StartsWith("e4mc", StringComparison.Ordinal),
                    LanServerPropertiesSlug => name.Contains("lanserverproperties", StringComparison.Ordinal),
                    _ => false,
                };

                // 装 LAN World Plug-n-Play 时顺手清掉 e4mc 与 LAN Server Properties：
                // 三者都接管「对局域网开放」界面，同时存在会互相抢同一块 UI，行为不可预期。
                if (!isSame && kind == LanModKind.McWifiPnp)
                {
                    isSame = name.StartsWith("e4mc", StringComparison.Ordinal)
                             || name.Contains("lanserverproperties", StringComparison.Ordinal);
                }

                // 反过来，如果用户后来手动装了旧模组，也不要让同一个 mcwifipnp 留两份。
                if (!isSame && slug != McWifiPnpSlug && kind is LanModKind.E4mc or LanModKind.LanServerProperties)
                    isSame = name.StartsWith("mcwifipnp", StringComparison.Ordinal);

                if (!isSame)
                    continue;

                File.Delete(jar);
                removed.Add(Path.GetFileName(jar));
                Serilog.Log.Information("联机模组：删除旧文件 {File}", Path.GetFileName(jar));
            }
        }
        catch (Exception ex)
        {
            // 删不掉（被占用）不该阻断安装，最多留下一个重复 mod
            Serilog.Log.Warning(ex, "清理旧联机模组失败：{Folder}", modsFolder);
        }

        return removed;
    }

    /// <summary>实例加载器 → Modrinth 的 loaders 标签。</summary>
    private static string? LoaderTag(ModLoaderKind loader) => loader switch
    {
        ModLoaderKind.Fabric => "fabric",
        ModLoaderKind.Quilt => "quilt",
        ModLoaderKind.Forge => "forge",
        ModLoaderKind.NeoForge => "neoforge",
        _ => null,
    };

    private static LanModInstallResult Fail(string message)
        => new() { Success = false, Message = message };
}
