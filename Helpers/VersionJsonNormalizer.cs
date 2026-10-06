using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 版本 JSON 规范化。
///
/// 存在的意义是绕开 MinecraftLaunch 4.0.7 的一个解析器 bug：
/// <c>MinecraftParser.IsVanilla()</c> 会拿版本 JSON 里的 <c>arguments</c> 直接调
/// <c>JsonElement.TryGetProperty("game")</c>。而 **1.13 以前的版本（含 1.12.2）用的是
/// 老式的 <c>minecraftArguments</c> 字符串，压根没有 arguments 字段** —— 此时该
/// JsonElement 的 ValueKind 是 Undefined，System.Text.Json 会抛
/// <c>InvalidOperationException</c>，文案正是「Operation is not valid due to the
/// current state of the object.」。
///
/// 同一处 bug 还存在于 <c>GameArgumentParser.Parse</c> 与 <c>JvmArgumentParser.Parse</c>，
/// 它们也无条件对 <c>Arguments</c> 调 TryGetProperty，所以**老版本不仅装不上，连启动都会炸**。
///
/// 修法：给缺 <c>arguments</c> 的版本 JSON 补一个只有空 game 数组的对象
/// <c>"arguments": {"game": []}</c>（**不能带 jvm 键**，原因见 EmptyArguments 的注释）。
/// 这样三个解析器都能正常走完，且生成的启动参数与官方启动器一致。
///
/// 另一个职责是修正 <c>+HHMM</c> 形式的时区（老版本 JSON 里 releaseTime / time 字段
/// 写的是 <c>2017-09-18T11:39:35+00:00</c> 还是 <c>+0000</c> 取决于生成方，
/// 后者 System.Text.Json 解析不了），与 App 启动时的扫描保持一致。
/// </summary>
public static class VersionJsonNormalizer
{
    /// <summary>+HHMM 形式的时区修正为 +HH:MM。</summary>
    private static readonly Regex TimeZoneRegex = new(@"([+-]\d{2})(\d{2})""");

    /// <summary>
    /// 补进 JSON 的空 arguments 对象（末尾带逗号，插在根对象开括号之后）。
    ///
    /// 注意里面**只放 game、不放 jvm** —— 这一点是踩过坑的：
    /// <c>JvmArgumentParser.Parse</c> 用「有没有 jvm 键」来区分新旧两套 JVM 参数。
    /// 一旦写了 <c>"jvm": []</c>，它会走「现代版」分支，只吐一个 <c>-cp</c>，
    /// **丢掉 <c>-Djava.library.path=${natives_directory}</c>**，老版本进游戏就直接
    /// 找不到 LWJGL 原生库。留空键则回到老式分支，参数与官方启动器一致。
    /// game 写空数组是安全的：GameArgumentParser 会先无条件展开 minecraftArguments，
    /// 再追加 arguments.game。
    /// </summary>
    private const string EmptyArguments = "\"arguments\": {\"game\": []},";

    /// <summary>
    /// 规范化一份版本 JSON 文本。
    /// </summary>
    /// <returns>规范化后的文本，以及内容是否真的发生了变化。</returns>
    public static (string Json, bool Changed) Normalize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (json, false);

        var changed = false;

        // 时区必须先修：格式不对时后面连 JsonDocument 都解析不了
        if (TimeZoneRegex.IsMatch(json))
        {
            json = TimeZoneRegex.Replace(json, "$1:$2\"");
            changed = true;
        }

        switch (ProbeArguments(json))
        {
            case ArgumentsState.Missing:
            {
                var brace = json.IndexOf('{');
                if (brace < 0)
                    break;

                json = string.Concat(json.AsSpan(0, brace + 1), "\n  ", EmptyArguments, json.AsSpan(brace + 1));
                changed = true;
                break;
            }

            case ArgumentsState.NotAnObject:
                // 理论上不会出现（官方与各加载器要么不写、要么写成对象）。
                // 这种情况改起来要重排整份 JSON，风险大于收益，这里只留日志不动文件。
                Serilog.Log.Warning("版本 JSON 的 arguments 字段不是对象，已跳过修补");
                break;
        }

        return (json, changed);
    }
    /// <summary>
    /// 就地规范化磁盘上的版本 JSON。已经是规范格式的文件不会被重写。
    /// </summary>
    /// <returns>文件是否被改写。</returns>
    public static bool NormalizeFile(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath))
                return false;

            var (normalized, changed) = Normalize(File.ReadAllText(jsonPath));
            if (!changed)
                return false;

            File.WriteAllText(jsonPath, normalized);
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "规范化版本 JSON 失败：{Path}", jsonPath);
            return false;
        }
    }

    private enum ArgumentsState
    {
        /// <summary>已经是一个对象，可以直接交给库解析。</summary>
        Usable,

        /// <summary>整个字段都没有。</summary>
        Missing,

        /// <summary>字段存在但不是对象（null / 数组 / 字符串等）。</summary>
        NotAnObject,
    }

    private static ArgumentsState ProbeArguments(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return ArgumentsState.NotAnObject;

            if (!document.RootElement.TryGetProperty("arguments", out var arguments))
                return ArgumentsState.Missing;

            return arguments.ValueKind == JsonValueKind.Object
                ? ArgumentsState.Usable
                : ArgumentsState.NotAnObject;
        }
        catch (JsonException)
        {
            // 解析不了就当作缺失处理，补进去没有任何副作用
            return ArgumentsState.Missing;
        }
    }
}
