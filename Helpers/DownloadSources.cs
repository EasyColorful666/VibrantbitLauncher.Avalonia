using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Helpers;

/// <summary>下载源的实测表现，用于在本次会话内把慢/坏的源降级。</summary>
public enum SourceHealth
{
    /// <summary>正常。</summary>
    Good,

    /// <summary>能通但很慢 —— 不换源的话整个安装过程都会被它拖住。</summary>
    Slow,

    /// <summary>连接失败 / 超时 / 报错。</summary>
    Failed,
}

/// <summary>用户填的一行自定义源里识别不了的那部分，连同原因。</summary>
public sealed record CustomSourceIssue(string Raw, string Reason);

/// <summary>
/// 下载源改写：把官方 URL 映射到镜像，并按用户设置排出「先试哪个」的顺序。
///
/// 为什么不直接用 MinecraftLaunch 自带的 <c>MinecraftLaunch.BmclApiSource.TryFindUrl</c>：
/// 它依赖全局的 <c>IsEnableMirror</c> 开关，**关着的时候原样返回**，
/// 于是「官方源连不上 → 自动改走镜像」这条兜底路径根本没法实现 ——
/// 而恰恰是这条路径能救掉「用默认设置装 Fabric 卡到超时」的场景。
/// 这里把映射表自己维护一份，既能主动改写，也能在失败时立刻换源。
///
/// 映射表只收录**已实测返回 200 / 302 的主机**（2026-10-07 验证）。
/// 没验证过的一律不猜：改出一个不存在的路径，只会白白多耗一次超时，
/// 比不改写更糟。所以 Quilt 的 meta 端点暂时不在表内（库的 maven 在）。
///
/// <para>
/// <b>根地址是可替换的</b>（1.0.32.0）：默认根是 BMCLAPI，用户也可以自己填一个
/// 「BMCLAPI 兼容」的镜像根（设置 → 网络 → 自定义下载源）。所有路径都按
/// <c>根地址 + /maven/</c> 这种形式拼，因此换根等于换了一家镜像商，
/// 不需要为每家维护一份映射表。
/// </para>
///
/// 另外还带一层**源健康度**：某个主机一旦被实测为「慢」或「坏」，
/// 在接下来一段时间里会被排到候选末尾。批量下载时这一层很关键 ——
/// 实测官方 maven.fabricmc.net 在国内经常是「能通但每 MB 要几十秒」，
/// 没有它的话 8 个库会全部走慢源，安装从几秒变成近三分钟。
/// </summary>
public static class DownloadSources
{
    /// <summary>默认根：BMCLAPI。</summary>
    public const string BmclMirrorRoot = "https://bmclapi2.bangbang93.com";

    /// <summary>内置根地址（顺序即改写顺序，目前只有一个）。</summary>
    private static readonly string[] BuiltInRoots = { BmclMirrorRoot };

    /// <summary>被标记为慢/坏之后，多久内保持降级。</summary>
    private static readonly TimeSpan PenaltyDuration = TimeSpan.FromMinutes(10);

    /// <summary>主机名 → 降级截止时刻（<see cref="Environment.TickCount64"/> 时间轴）。</summary>
    private static readonly ConcurrentDictionary<string, long> PenaltyUntil =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 官方前缀 → 镜像侧路径（以 <c>/</c> 开头，拼在根地址后面）。顺序无关，前缀互不包含。
    /// </summary>
    private static readonly (string Official, string Path)[] Map =
    {
        ("https://meta.fabricmc.net/", "/fabric-meta/"),
        ("https://maven.fabricmc.net/", "/maven/"),
        ("https://maven.minecraftforge.net/", "/maven/"),
        ("https://files.minecraftforge.net/maven/", "/maven/"),
        ("https://libraries.minecraft.net/", "/maven/"),
        // NeoForge 的制品全在 maven.neoforged.net 上，国内基本连不上（实测 curl 直接连接失败）。
        // BMCLAPI 有完整镜像，路径是 /maven/ + 原路径 —— 实测 1.20.1 的
        // /maven/net/neoforged/forge/1.20.1-47.1.105/forge-1.20.1-47.1.105-installer.jar 返回 200 且
        // 是完整 7.8 MB 包。注意 NeoForge 1.20.1 的制品在 net/neoforged/forge 下，
        // 1.20.2+ 才是 net/neoforged/neoforge，映射按前缀整段替换，两者都覆盖。
        ("https://maven.neoforged.net/releases/", "/maven/"),
        // Quilt 的库在 maven.quiltmc.org 上；BMCLAPI 有镜像（实测 quilt-loader 返回 200）。
        ("https://maven.quiltmc.org/repository/release/", "/maven/"),
    };

    // ==================== 自定义根地址 ====================

    /// <summary>上次解析用的原文，仅用于避免每次取 URL 都重新切一遍字符串。</summary>
    private static string _customRaw = "\u0000";

    private static string[] _customRoots = Array.Empty<string>();
    private static CustomSourceIssue[] _customIssues = Array.Empty<CustomSourceIssue>();

    private static readonly object CustomLock = new();

    /// <summary>
    /// 把用户填的多行文本解析成根地址列表。
    ///
    /// <para>
    /// 容错口径：空行、<c>#</c> 注释、逗号 / 分号分隔都接受（用户常常整段粘过来）；
    /// 末尾的 <c>/</c> 一律去掉，避免拼出 <c>//maven/</c>；重复项只保留第一次出现的位置。
    /// 只认 <c>http</c> / <c>https</c> 绝对地址 —— 少了协议名的字符串拼出来必然是坏 URL，
    /// 与其下载时逐个超时，不如现在就报出来。
    /// </para>
    /// </summary>
    public static (IReadOnlyList<string> Roots, IReadOnlyList<CustomSourceIssue> Issues) ParseCustomSources(string? raw)
    {
        var roots = new List<string>();
        var issues = new List<CustomSourceIssue>();

        if (string.IsNullOrWhiteSpace(raw))
            return (roots, issues);

        var tokens = raw.Split(
            new[] { '\r', '\n', ',', '，', ';', '；' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (token.Length == 0 || token.StartsWith('#'))
                continue;

            var candidate = token.Trim().TrimEnd('/');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host))
            {
                issues.Add(new CustomSourceIssue(token, "需要以 http:// 或 https:// 开头的完整地址"));
                continue;
            }

            // 根地址不接受带路径的输入：拼 /maven/ 时多一层路径必然是 404，
            // 与其让用户以为「填对了但很慢」，不如直说。
            if (uri.AbsolutePath is not ("" or "/"))
            {
                issues.Add(new CustomSourceIssue(token, "只填到域名即可，不要带路径"));
                continue;
            }

            var normalized = $"{uri.Scheme}://{uri.Authority}";
            if (!roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                roots.Add(normalized);
        }

        return (roots, issues);
    }

    private static void RefreshCustomSources()
    {
        var raw = SettingsService.Current.CustomDownloadSources ?? string.Empty;
        if (string.Equals(raw, _customRaw, StringComparison.Ordinal))
            return;

        lock (CustomLock)
        {
            if (string.Equals(raw, _customRaw, StringComparison.Ordinal))
                return;

            var (roots, issues) = ParseCustomSources(raw);
            _customRoots = roots.ToArray();
            _customIssues = issues.ToArray();
            _customRaw = raw;
        }
    }

    /// <summary>用户配置里**可用的**自定义根地址（已规范化，顺序即优先级）。</summary>
    public static IReadOnlyList<string> CustomMirrorRoots
    {
        get
        {
            RefreshCustomSources();
            return _customRoots;
        }
    }

    /// <summary>用户配置里识别不了的条目（设置页用来逐条提示）。</summary>
    public static IReadOnlyList<CustomSourceIssue> CustomSourceIssues
    {
        get
        {
            RefreshCustomSources();
            return _customIssues;
        }
    }

    /// <summary>
    /// 这次下载实际会用的镜像根地址。
    ///
    /// 非「自定义」模式只有 BMCLAPI；自定义模式下一旦一个有效地址都没有，
    /// 退回 BMCLAPI —— 宁可走默认镜像，也不要让整个下载链路没有镜像可用。
    /// </summary>
    public static IReadOnlyList<string> ActiveMirrorRoots
    {
        get
        {
            if (SettingsService.DownloadSourceMode != "Custom")
                return BuiltInRoots;

            var roots = CustomMirrorRoots;
            return roots.Count > 0 ? roots : BuiltInRoots;
        }
    }

    /// <summary>选了自定义源但一个有效地址都没有（设置页据此提示「已退回内置镜像」）。</summary>
    public static bool CustomSourceFallsBackToBuiltIn =>
        SettingsService.DownloadSourceMode == "Custom" && CustomMirrorRoots.Count == 0;

    // ==================== URL 改写 ====================

    /// <summary>官方地址 → 首选镜像地址；该主机没有可用镜像时原样返回。</summary>
    public static string ToMirror(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        var roots = ActiveMirrorRoots;
        if (roots.Count == 0)
            return url;

        var root = roots[0];
        foreach (var (official, path) in Map)
        {
            if (url.StartsWith(official, StringComparison.OrdinalIgnoreCase))
                return root + path + url[official.Length..];
        }

        return url;
    }

    /// <summary>该地址是否存在可用镜像（即改写结果与原文不同）。</summary>
    public static bool HasMirror(string url)
        => !string.Equals(ToMirror(url), url, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 该地址**本身就是镜像站**（当前任一镜像根的入口）。
    ///
    /// 用来区分「官方源慢」和「镜像慢」这两种完全不同的情况：
    /// 镜像的 <c>/maven/</c> 会 302 到一批志愿者节点，具体落到哪个节点每次都不一样 ——
    /// 一次下载慢只说明这次抽到的节点慢，不代表镜像站坏了。
    /// </summary>
    public static bool IsMirrorHost(string url) => IsMirrorHost(url, ActiveMirrorRoots);

    /// <summary>按给定根地址判断（体检 / 测速 / 单元验证都走这个纯函数）。</summary>
    internal static bool IsMirrorHost(string url, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        if (StartsWithRoot(url, BmclMirrorRoot))
            return true;

        foreach (var root in roots)
        {
            if (StartsWithRoot(url, root))
                return true;
        }

        return false;
    }

    private static bool StartsWithRoot(string url, string root)
        => url.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
           || string.Equals(url.TrimEnd('/'), root, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 原版版本 JSON 的镜像地址（首选根）。
    ///
    /// 镜像端点是 <c>/version/&lt;id&gt;/json</c>，并不是对官方 URL 做路径前缀替换
    /// （官方是 <c>piston-meta.mojang.com/v1/packages/&lt;hash&gt;/&lt;id&gt;.json</c>，两者毫无对应关系），
    /// 所以单独开一个方法而不是塞进 <see cref="Map"/>。id 为空时返回 null。
    /// </summary>
    public static string VersionJsonMirror(string versionId)
        => string.IsNullOrWhiteSpace(versionId) ? null : $"{ActiveMirrorRoots[0]}/version/{versionId}/json";

    /// <summary>每个镜像根都来一份版本 JSON 地址（多重自定义源时互为兜底）。</summary>
    public static IReadOnlyList<string> VersionJsonMirrors(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
            return Array.Empty<string>();

        return ActiveMirrorRoots
            .Select(root => $"{root}/version/{versionId}/json")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>取 URL 里的主机名，用于日志、提示文案与健康度记账。</summary>
    public static string HostOf(string url)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        var start = url.IndexOf("://", StringComparison.Ordinal);
        start = start < 0 ? 0 : start + 3;
        var end = url.IndexOf('/', start);
        return end < 0 ? url[start..] : url[start..end];
    }

    /// <summary>
    /// 记录一次实测结果。下载器每完成/失败一个请求都应当回报一次 ——
    /// 这是「第一个库慢了，后面 7 个自动绕开」的依据。
    /// </summary>
    public static void ReportSourceHealth(string url, SourceHealth health)
    {
        var host = HostOf(url);
        if (string.IsNullOrEmpty(host))
            return;

        if (health == SourceHealth.Good)
            PenaltyUntil.TryRemove(host, out _);
        else
            PenaltyUntil[host] = Environment.TickCount64 + (long)PenaltyDuration.TotalMilliseconds;
    }

    /// <summary>主机当前是否处于降级状态。</summary>
    public static bool IsDemoted(string url)
    {
        var host = HostOf(url);
        if (string.IsNullOrEmpty(host) || !PenaltyUntil.TryGetValue(host, out var until))
            return false;

        if (Environment.TickCount64 >= until)
        {
            PenaltyUntil.TryRemove(host, out _);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 同一个文件在**其它镜像根**上的地址（不含 <paramref name="url"/> 自身）。
    ///
    /// <para>
    /// 两种情况都要处理：官方地址 → 各根的改写结果；已经是某个根的地址 → 换成别的根。
    /// 后者是「多重自定义源」的关键：版本 JSON 在批量下载里是以镜像地址进队的，
    /// 只按官方前缀匹配的话，第二个根永远排不上用场。
    /// </para>
    /// </summary>
    private static List<string> MirrorVariants(string url, IReadOnlyList<string> roots)
    {
        var variants = new List<string>();

        foreach (var (official, path) in Map)
        {
            if (!url.StartsWith(official, StringComparison.OrdinalIgnoreCase))
                continue;

            var suffix = url[official.Length..];
            foreach (var root in roots)
                variants.Add(root + path + suffix);

            return variants;
        }

        foreach (var root in roots)
        {
            if (!StartsWithRoot(url, root))
                continue;

            var suffix = url[root.Length..];
            foreach (var other in roots)
            {
                if (!string.Equals(other, root, StringComparison.OrdinalIgnoreCase))
                    variants.Add(other + suffix);
            }

            return variants;
        }

        return variants;
    }

    /// <summary>
    /// 依次尝试的候选地址。用户设置只决定**谁先谁后**，两个源都败才算失败。
    ///
    /// 这正是「HTTP 超时」那个 bug 的关键：原来只有一个源，
    /// 一旦卡住就只能干等到总超时，哪怕镜像明明可用。
    ///
    /// 被实测降级的主机会被稳定地挪到末尾（不会移除 —— 万一两边都不行，
    /// 还是得有人去试，只是顺序不同）。
    /// </summary>
    public static IReadOnlyList<string> BuildCandidates(string url)
        => BuildCandidates(url, SettingsService.DownloadSourceMode, ActiveMirrorRoots);

    /// <summary>
    /// 候选地址的**纯函数版本**：模式与根地址都由调用方传入，
    /// 不读全局设置。体检 / 测速预览、以及离线验证都用这一版，
    /// 免得为了「假如选了某个源会怎样」而真去改配置。
    /// </summary>
    internal static IReadOnlyList<string> BuildCandidates(string url, string mode, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrEmpty(url))
            return Array.Empty<string>();

        var variants = MirrorVariants(url, roots);

        var list = new List<string>(variants.Count + 2);

        void Add(string? candidate)
        {
            if (!string.IsNullOrEmpty(candidate)
                && !list.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(candidate);
            }
        }

        switch (mode)
        {
            // 「固定镜像」：只走镜像，不做官方兜底。
            // 给的是「我确定这台机器连不上 Mojang，别浪费时间试官方」这种场景。
            case "Mirror":
                foreach (var variant in variants) Add(variant);
                if (list.Count == 0) Add(url);
                break;

            // 「自定义源」：自定义根优先，官方兜底（自定义地址多半是自建代理，
            // 它挂了的时候官方往往还能通）。
            case "Custom":
                foreach (var variant in variants) Add(variant);
                Add(url);
                break;

            // 「官方优先」：官方在前，镜像在后。
            case "Official":
                if (!IsMirrorHost(url, roots)) Add(url);
                foreach (var variant in variants) Add(variant);
                if (list.Count == 0) Add(url);
                break;

            // 「自动测速」：镜像在前、官方在后，与历史行为一致。
            default:
                foreach (var variant in variants) Add(variant);
                Add(url);
                break;
        }

        // 稳定排序：Equal 的保持原顺序，所以「用户偏好」在健康度相同的情况下依旧生效
        return list.OrderBy(u => IsDemoted(u) ? 1 : 0).ToList();
    }
}
