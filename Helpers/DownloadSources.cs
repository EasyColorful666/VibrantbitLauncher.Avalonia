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

/// <summary>
/// 下载源改写：把官方 URL 映射到 BMCLAPI 镜像，并按用户设置排出「先试哪个」的顺序。
///
/// 为什么不直接用 MinecraftLaunch 自带的 <c>MinecraftLaunch.BmclApiSource.TryFindUrl</c>：
/// 它依赖全局的 <c>IsEnableMirror</c> 开关，**关着的时候原样返回**，
/// 于是「官方源连不上 → 自动改走镜像」这条兜底路径根本没法实现 ——
/// 而恰恰是这条路径能救掉「用默认设置装 Fabric 卡到超时」的场景。
/// 这里把映射表自己维护一份，既能主动改写，也能在失败时立刻换源。
///
/// 映射表只收录**已实测返回 200 / 302 的主机**（2026-10-07 验证）。
/// 没验证过的一律不猜：改出一个不存在的路径，只会白白多耗一次超时，
/// 比不改写更糟。所以 Quilt（meta.quiltmc.org / maven.quiltmc.org）暂时不在表内，
/// 行为与改动前一致（单源直连）。
///
/// 另外还带一层**源健康度**：某个主机一旦被实测为「慢」或「坏」，
/// 在接下来一段时间里会被排到候选末尾。批量下载时这一层很关键 ——
/// 实测官方 maven.fabricmc.net 在国内经常是「能通但每 MB 要几十秒」，
/// 没有它的话 8 个库会全部走慢源，安装从几秒变成近三分钟。
/// </summary>
public static class DownloadSources
{
    private const string MirrorRoot = "https://bmclapi2.bangbang93.com";

    /// <summary>被标记为慢/坏之后，多久内保持降级。</summary>
    private static readonly TimeSpan PenaltyDuration = TimeSpan.FromMinutes(10);

    /// <summary>主机名 → 降级截止时刻（<see cref="Environment.TickCount64"/> 时间轴）。</summary>
    private static readonly ConcurrentDictionary<string, long> PenaltyUntil =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>官方前缀 → 镜像前缀。顺序无关，前缀互不包含。</summary>
    private static readonly (string Official, string Mirror)[] Map =
    {
        ("https://meta.fabricmc.net/", MirrorRoot + "/fabric-meta/"),
        ("https://maven.fabricmc.net/", MirrorRoot + "/maven/"),
        ("https://maven.minecraftforge.net/", MirrorRoot + "/maven/"),
        ("https://files.minecraftforge.net/maven/", MirrorRoot + "/maven/"),
        ("https://libraries.minecraft.net/", MirrorRoot + "/maven/"),
        // NeoForge 的制品全在 maven.neoforged.net 上，国内基本连不上（实测 curl 直接连接失败）。
        // BMCLAPI 有完整镜像，路径是 /maven/ + 原路径 —— 实测 1.20.1 的
        // /maven/net/neoforged/forge/1.20.1-47.1.105/forge-1.20.1-47.1.105-installer.jar 返回 200 且
        // 是完整 7.8 MB 包。注意 NeoForge 1.20.1 的制品在 net/neoforged/forge 下，
        // 1.20.2+ 才是 net/neoforged/neoforge，映射按前缀整段替换，两者都覆盖。
        ("https://maven.neoforged.net/releases/", MirrorRoot + "/maven/"),
        // Quilt 的库在 maven.quiltmc.org 上；BMCLAPI 有镜像（实测 quilt-loader 返回 200）。
        ("https://maven.quiltmc.org/repository/release/", MirrorRoot + "/maven/"),
    };

    /// <summary>官方地址 → 镜像地址；该主机没有可用镜像时原样返回。</summary>
    public static string ToMirror(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        foreach (var (official, mirror) in Map)
        {
            if (url.StartsWith(official, StringComparison.OrdinalIgnoreCase))
                return mirror + url[official.Length..];
        }

        return url;
    }

    /// <summary>该地址是否存在可用镜像（即改写结果与原文不同）。</summary>
    public static bool HasMirror(string url)
        => !string.Equals(ToMirror(url), url, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 该地址**本身就是镜像站**（BMCLAPI 的某个入口）。
    ///
    /// <para>
    /// 用来区分「官方源慢」和「镜像慢」这两种完全不同的情况：BMCLAPI 的 <c>/maven/</c>
    /// 会 302 到一批志愿者节点，具体落到哪个节点每次都不一样 ——
    /// 一次下载慢只说明这次抽到的节点慢，不代表镜像站坏了。
    /// </para>
    /// </summary>
    public static bool IsMirrorHost(string url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        foreach (var (_, mirror) in Map)
        {
            if (url.StartsWith(mirror, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 原版版本 JSON 的镜像地址。
    ///
    /// BMCLAPI 的对应端点是 <c>/version/&lt;id&gt;/json</c>，并不是对官方 URL 做路径前缀替换
    /// （官方是 <c>piston-meta.mojang.com/v1/packages/&lt;hash&gt;/&lt;id&gt;.json</c>，两者毫无对应关系），
    /// 所以单独开一个方法而不是塞进 <see cref="Map"/>。id 为空时返回 null。
    /// </summary>
    public static string VersionJsonMirror(string versionId)
        => string.IsNullOrWhiteSpace(versionId) ? null : $"{MirrorRoot}/version/{versionId}/json";

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
    /// 依次尝试的候选地址。用户设置只决定**谁先谁后**，两个源都败才算失败。
    ///
    /// 这正是本次「HTTP 超时」bug 的关键：原来只有一个源，
    /// 一旦卡住就只能干等到 100 秒总超时，哪怕镜像明明可用。
    ///
    /// 被实测降级的主机会被稳定地挪到末尾（不会移除 —— 万一两边都不行，
    /// 还是得有人去试，只是顺序不同）。
    /// </summary>
    public static IReadOnlyList<string> BuildCandidates(string url)
    {
        if (string.IsNullOrEmpty(url))
            return Array.Empty<string>();

        var mirror = ToMirror(url);
        var preferMirror = SettingsService.Current.UseBmclMirror;

        var first = preferMirror ? mirror : url;
        var second = preferMirror ? url : mirror;

        var list = new List<string>(2);
        foreach (var candidate in new[] { first, second })
        {
            if (!string.IsNullOrEmpty(candidate) &&
                !list.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(candidate);
            }
        }

        // 稳定排序：Equal 的保持原顺序，所以「用户偏好」在健康度相同的情况下依旧生效
        return list.OrderBy(u => IsDemoted(u) ? 1 : 0).ToList();
    }
}
