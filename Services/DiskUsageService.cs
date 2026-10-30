using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Serilog;

namespace VibrantbitLauncher.Services;

/// <summary>某一块的磁盘占用。</summary>
/// <param name="Key">稳定标识（game / logs / core），用于在界面里区分行。</param>
/// <param name="Title">面向用户的名称。</param>
/// <param name="Path">对应目录的绝对路径。</param>
/// <param name="Bytes">占用字节数；目录不存在时为 0。</param>
/// <param name="Exists">目录是否存在（不存在时界面要说明「还没生成」，而不是显示 0 B）。</param>
public sealed record DiskUsageEntry(string Key, string Title, string Path, long Bytes, bool Exists)
{
    /// <summary>给界面直接显示的大小文本，如「1.2 GB」。</summary>
    public string SizeText => Exists ? DiskUsageService.FormatSize(Bytes) : "未生成";
}

/// <summary>
/// 统计启动器相关目录的磁盘占用。
///
/// <para>
/// 玩家最常问的一句话是「怎么占了这么大」，而答案往往分散在三处：游戏目录（实例 + 存档 + 模组）、
/// 日志、联机内核。以前只能自己一层层翻文件夹看属性。
/// </para>
///
/// <para>
/// 统计**必须**放到后台线程：一个装满模组和存档的 .minecraft 有几万个小文件，
/// 在机械盘上枚举要好几秒，放在 UI 线程就是整页卡住。
/// 并且要跳过重解析点 —— 项目会把 resourcepacks / saves 建成目录链接指到别的盘，
/// 顺着链接走会把整块外接盘也算进来，链接成环还会死循环。
/// </para>
/// </summary>
public static class DiskUsageService
{
    private static readonly EnumerationOptions EnumOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
    };

    /// <summary>把三块目录都量一遍。整体放后台线程，调用方直接 await。</summary>
    public static Task<IReadOnlyList<DiskUsageEntry>> MeasureAsync() => Task.Run<IReadOnlyList<DiskUsageEntry>>(() =>
    {
        var targets = new (string Key, string Title, string Path)[]
        {
            ("game", "游戏目录", SettingsService.ResolveMinecraftFolder()),
            ("logs", "日志", LogService.LogDirectory),
            ("core", "联机内核", EasyTierService.VblDirectory)
        };

        var result = new List<DiskUsageEntry>(targets.Length);
        foreach (var (key, title, path) in targets)
        {
            var exists = false;
            var bytes = 0L;

            try
            {
                exists = Directory.Exists(path);
                if (exists)
                    bytes = MeasureDirectory(path);
            }
            catch (Exception ex)
            {
                // 量不出来不该让整页报错 —— 填 0，剩下的照常显示
                Log.Debug(ex, "统计目录占用失败：{Path}", path);
            }

            result.Add(new DiskUsageEntry(key, title, path, bytes, exists));
        }

        return result;
    });

    /// <summary>递归求目录总大小。跳过重解析点与系统文件，读不到的项略过。</summary>
    public static long MeasureDirectory(string path)
    {
        var total = 0L;

        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", EnumOptions))
        {
            try
            {
                total += file.Length;
            }
            catch
            {
                // 单个文件读不到长度（权限 / 已被删除）就跳过
            }
        }

        return total;
    }

    /// <summary>按配置里的保留天数清掉超期日志，返回删除的文件数。当天的日志永远保留。</summary>
    public static int CleanExpiredLogs()
    {
        var days = Math.Max(1, SettingsService.Current.LogRetentionDays);
        var cutoff = DateTime.Now.AddDays(-days);
        var deleted = 0;

        foreach (var file in LogService.GetLogFiles())
        {
            try
            {
                if (file.LastWriteTime >= cutoff)
                    continue;

                file.Delete();
                deleted++;
            }
            catch
            {
                // 被别的进程占着（比如另开了一个启动器）就留着，下次再说
            }
        }

        if (deleted > 0)
            Log.Information("已清理 {Count} 个超期日志文件", deleted);

        return deleted;
    }

    /// <summary>把字节数写成「1.2 GB」这种形式。界面上一律用这个，避免各处各写一套。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        double value = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        var unit = -1;

        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        return value >= 100
            ? $"{value:F0} {units[unit]}"
            : $"{value:F1} {units[unit]}";
    }
}
