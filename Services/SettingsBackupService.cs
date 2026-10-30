using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Serilog;

namespace VibrantbitLauncher.Services;

/// <summary>导入配置的结果。</summary>
/// <param name="Applied">真正改动的项数。</param>
/// <param name="Skipped">认出来了但没动（敏感字段 / 本机路径）。</param>
/// <param name="Error">失败原因；为 null 表示导入成功。</param>
public sealed record SettingsImportResult(int Applied, int Skipped, string? Error)
{
    public bool Succeeded => Error is null;

    public static SettingsImportResult Fail(string error) => new(0, 0, error);
}

/// <summary>
/// 设置备份：导出一份可读的 <c>.json</c>，导入时逐项合并。
///
/// <para>
/// 存在的理由有三个：换电脑不想重配一遍；给朋友抄一份自己的参数；改坏了想退回上一版。
/// 全局「恢复默认」解决的是「全都乱了」，它解决不了「上周那套挺好，能不能退回去」。
/// </para>
///
/// <para>
/// <b>不上车的东西</b>（见 <see cref="Excluded"/>）：账户凭证、联机密钥、本机路径、
/// 跟这台机器绑定的性能开关、以及语言偏好。前几个是隐私与安全问题 ——
/// 备份文件是要发给别人的，里面绝不能有登录令牌；后几个换了机器就没意义。
/// </para>
/// </summary>
public static class SettingsBackupService
{
    private const string FormatTag = "vibrantbit-settings";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 出现在配置里、但**不写进备份**的字段。
    ///
    /// <para>
    /// 判断标准只有两条：泄露了会不会出事（凭证 / 密钥），以及换台机器还用不用得上（路径 / 性能）。
    /// 新增设置项时默认是「会被备份」的，只有确属这两类才需要往这里加。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        // —— 账户与凭证 ——
        nameof(AppSettings.MicrosoftAccounts),
        nameof(AppSettings.YggdrasilAccounts),
        nameof(AppSettings.OfflineAccounts),
        nameof(AppSettings.SelectedAccountUuid),
        nameof(AppSettings.IsMicrosoftAccount),
        nameof(AppSettings.IsFirstRun),

        // —— 联机密钥 ——
        nameof(AppSettings.EasyTierNetworkKey),
        nameof(AppSettings.LanModInstanceId),

        // —— 本机路径 / 本机文件 ——
        nameof(AppSettings.MinecraftFolder),
        nameof(AppSettings.JavaPath),
        nameof(AppSettings.JavaRuntimeRoot),
        nameof(AppSettings.ResourcePacksFolder),
        nameof(AppSettings.SavesFolder),
        nameof(AppSettings.BackgroundImagePath),
        nameof(AppSettings.FontFamily),

        // —— 跟这台机器绑定 ——
        nameof(AppSettings.PerformanceMode),
        nameof(AppSettings.PerformanceModeAsked),

        // —— 个人偏好，不该被别人的备份改掉 ——
        nameof(AppSettings.Language),

        // 主题包是装在**这台机器**上的东西：备份里只带一个 id，
        // 到了没装那套主题的机器上就是一个悬空引用。宁可回默认，也不要加载一套不存在的主题。
        nameof(AppSettings.ThemePackage),

        // —— 已废弃字段，只用于读老配置 ——
        nameof(AppSettings.DownloadThreads)
    };

    /// <summary>导出一份备份，返回写进文件的项数。会剔除敏感与本机相关字段。</summary>
    public static int Export(string path)
    {
        var settings = new JsonObject();
        var count = 0;

        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (Excluded.Contains(property.Name) || !IsSimple(property.PropertyType))
                continue;

            var value = property.GetValue(SettingsService.Current);
            settings[property.Name] = ToNode(value);
            count++;
        }

        var payload = new JsonObject
        {
            ["format"] = FormatTag,
            ["version"] = FormatVersion,
            ["exportedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            ["launcherVersion"] = typeof(SettingsBackupService).Assembly.GetName().Version?.ToString(3)
                                  ?? "unknown",
            ["settings"] = settings
        };

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, payload.ToJsonString(WriteOptions));
        Log.Information("设置备份已导出：{Path}（{Count} 项）", path, count);
        return count;
    }

    /// <summary>
    /// 从备份文件导入。逐项比对后合并 —— 只改备份里有、且确实不同的项，
    /// 所以「拿一份旧备份覆盖回去」和「只补几个开关」是同一套逻辑。
    /// </summary>
    public static SettingsImportResult Import(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path).TrimStart('\uFEFF');
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取设置备份失败：{Path}", path);
            return SettingsImportResult.Fail("读不到这个文件");
        }

        JsonObject root;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject parsed)
                return SettingsImportResult.Fail("这个文件不是设置备份");

            root = parsed;
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "设置备份不是合法 JSON：{Path}", path);
            return SettingsImportResult.Fail("这个文件不是设置备份");
        }

        // 允许两种写法：带外壳的（本程序导出的）和「只有一个设置对象」的（手写的）
        var settings = root["settings"] as JsonObject ?? root;

        if (root["format"] is JsonValue tag
            && tag.TryGetValue<string>(out var tagText)
            && !string.Equals(tagText, FormatTag, StringComparison.Ordinal))
        {
            return SettingsImportResult.Fail("这个文件不是本启动器的设置备份");
        }

        var type = typeof(AppSettings);
        var applied = 0;
        var skipped = 0;

        foreach (var pair in settings)
        {
            // 「format」「version」这类外壳字段在裸配置写法里会混进来，跳过即可
            var property = type.GetProperty(pair.Key);
            if (property is null || !IsSimple(property.PropertyType))
                continue;

            if (Excluded.Contains(property.Name))
            {
                skipped++;
                continue;
            }

            object? value;
            try
            {
                value = pair.Value?.Deserialize(property.PropertyType);
            }
            catch (JsonException)
            {
                // 类型对不上的项（比如手改成了 "abc"）直接跳过，不因为一项坏掉整份备份
                skipped++;
                continue;
            }

            if (value is null)
            {
                skipped++;
                continue;
            }

            if (Equals(property.GetValue(SettingsService.Current), value))
                continue;

            property.SetValue(SettingsService.Current, value);
            applied++;
        }

        if (applied > 0)
        {
            SettingsService.Save();
            SettingsService.ApplyAll();
        }

        Log.Information("设置备份已导入：{Path}（改动 {Applied} 项，跳过 {Skipped} 项）", path, applied, skipped);
        return new SettingsImportResult(applied, skipped, null);
    }

    private static bool IsSimple(Type type) =>
        type == typeof(string) || type == typeof(bool) || type == typeof(int) || type == typeof(double);

    /// <summary>
    /// 把反射取出来的值包成 JSON 节点。
    ///
    /// <para>
    /// 不用 <c>JsonValue.Create(value)</c>：那里拿到的是 <c>object</c>，
    /// 泛型会被推断成 <c>object</c>，写出来的形态取决于实现细节而不是值的真实类型。
    /// 这里按实际类型逐个包，结果一眼可预期。
    /// </para>
    /// </summary>
    private static JsonNode? ToNode(object? value) => value switch
    {
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        _ => null
    };
}
