using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 「这个实例需要哪个 Java 主版本」。
///
/// <para>
/// 信息源是版本 JSON 里的 <c>javaVersion.majorVersion</c> —— 官方清单把它写得很明确
/// （1.17 → 16、1.20.5 → 21、1.21 → 21）。Fabric / Forge 这类加载器的 JSON 是
/// <c>inheritsFrom</c> 出来的，自己不带这段，所以缺失时要顺着父版本再找一层。
/// </para>
///
/// <para>
/// 两层都拿不到时（很老的版本、被裁剪的 JSON）按版本号推断：这是启动前的最后一道兜底，
/// 宁可给出一个能跑的旧版本，也不要因为读不到字段就拒绝启动。
/// </para>
/// </summary>
public static class JavaRequirement
{
    /// <summary>推断不出来时用的主版本：老版本的 JSON 普遍没有 javaVersion，Java 8 能兜住绝大多数。</summary>
    private const int FallbackMajor = 8;

    /// <summary>
    /// 解析某个实例需要的主版本号。返回 0 表示完全没有线索（调用方应视为「不限定」）。
    /// </summary>
    public static int Resolve(string mcFolder, string versionId)
    {
        var declared = ReadDeclaredMajor(mcFolder, versionId, depth: 3);
        if (declared > 0)
            return declared;

        var inferred = InferFromVersionId(versionId);
        Log.Information("版本 {Version} 的 JSON 没有 javaVersion，按版本号推断需要 Java {Major}",
            versionId, inferred);

        return inferred;
    }

    /// <summary>顺着 <c>inheritsFrom</c> 往上找 <c>javaVersion.majorVersion</c>。</summary>
    private static int ReadDeclaredMajor(string mcFolder, string versionId, int depth)
    {
        if (depth < 0 || string.IsNullOrWhiteSpace(versionId))
            return 0;

        try
        {
            var path = Path.Combine(mcFolder, "versions", versionId, versionId + ".json");
            if (!File.Exists(path))
                return 0;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("javaVersion", out var javaVersion)
                && javaVersion.TryGetProperty("majorVersion", out var majorElement)
                && majorElement.TryGetInt32(out var major)
                && major > 0)
                return major;

            if (root.TryGetProperty("inheritsFrom", out var parentElement)
                && parentElement.GetString() is { Length: > 0 } parent)
                return ReadDeclaredMajor(mcFolder, parent, depth - 1);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取 Java 版本要求失败：{Version}", versionId);
        }

        return 0;
    }

    /// <summary>
    /// 按版本号推断主版本。老编号是 <c>1.x</c>，2025 年之后改成了 <c>25.x / 26.x</c>，
    /// 后者一律 21 起。
    /// </summary>
    public static int InferFromVersionId(string versionId)
    {
        var match = Regex.Match(versionId ?? string.Empty, @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?");

        if (!match.Success)
            return FallbackMajor;

        var major = int.Parse(match.Groups[1].Value);

        // 新版本号（25.x / 26.x）：都是现代快照，最低 21
        if (major != 1)
            return 21;

        var minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;

        if (minor >= 21)
            return 21;

        if (minor == 20 && patch >= 5)
            return 21;

        return minor >= 17 ? 17 : FallbackMajor;
    }
}
