using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 极早期启动轨迹：**在托管代码的第一行就开始记**，用来区分「进程根本没起来」和
/// 「起来了但界面没出来」。
///
/// <para>
/// <b>为什么需要它（2026-10-21 用户反馈）</b>：用户反馈在另一台 Win10 机器上
/// 「能运行一次，之后再双击毫无反应」。这类症状有两种完全不同的根因：
/// </para>
/// <list type="number">
///   <item>
///     <b>宿主层失败</b>：单文件自解压要把原生库释放到 <c>%TEMP%\.net\…</c>，
///     之后被安全软件隔离 / 清理工具删掉 → 运行时在**任何托管代码之前**就退出。
///   </item>
///   <item>
///     <b>托管层失败</b>：代码跑起来了，但渲染初始化（ANGLE / D3D）崩溃 →
///     进程死得很快，用户同样看不到任何东西。
///   </item>
/// </list>
///
/// <para>
/// <c>StartupFailureReporter</c> 只能覆盖第 2 类，第 1 类连一个字节都留不下。
/// 这里在 <c>Program.Main</c> 的最开头写一条 <c>BEGIN</c>：<b>有 BEGIN = 托管起来了</b>，
/// <b>没有 BEGIN = 宿主层就挂了</b>。下一次再出问题，看一眼这个文件就能定性，
/// 不用再靠猜。
/// </para>
///
/// <para>
/// 落盘位置选 <c>%LOCALAPPDATA%</c> 而不是 exe 目录：启动器可能被放在 Program Files、
/// U 盘、或者一个只读的共享目录里 —— 那种情况下 exe 目录写不进去，而这里一定可写。
/// </para>
///
/// <para>
/// <b>本类的所有方法都绝不抛异常</b>：它服务于启动诊断，自己成为新的崩溃源就本末倒置了。
/// </para>
/// </summary>
internal static class StartupTrace
{
    /// <summary>写文件的互斥锁：多实例同时启动时避免写出半行。</summary>
    private static readonly object Gate = new();

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VibrantbitLauncher", "logs");

    private static readonly string LogPath = Path.Combine(LogDir, "startup-trace.log");

    /// <summary>超过这个大小就滚一次（只保留一份 .old），避免长期使用后无限膨胀。</summary>
    private const long MaxBytes = 512 * 1024;

    /// <summary>上一次（含之前连续）启动失败了几次。0 = 上次是正常结束的。</summary>
    public static int LastFailures { get; private set; }

    /// <summary>
    /// 读历史、算出「最近连续失败次数」，然后立刻写下本次的 BEGIN。
    ///
    /// 顺序不能反：先读历史再写本次，否则刚写进去的 BEGIN 会被自己算成一次失败。
    /// </summary>
    public static void BeginAndClassify()
    {
        LastFailures = CountRecentFailures();
        Write("BEGIN", DescribeRun());
    }

    /// <summary>主窗口已经真正显示出来 —— 这一次启动算成功。</summary>
    public static void MarkShown() => Write("SHOWN", string.Empty);

    /// <summary>应用生命周期结束（正常退出）。</summary>
    public static void MarkExit(string reason) => Write("EXIT", reason);

    /// <summary>捕获到异常（供崩溃上报链路顺手记一笔）。</summary>
    public static void MarkError(string summary) => Write("ERROR", summary);

    /// <summary>
    /// 统计「最近连续几次启动没能走到 MarkShown」。
    ///
    /// 从文件末尾往前扫：先遇到 <c>SHOWN</c> / <c>EXIT</c> 说明那一次是好的，直接收工；
    /// 先遇到 <c>BEGIN</c> 则那一次是失败的，继续往前数（最多 <paramref name="max"/> 次）。
    /// </summary>
    public static int CountRecentFailures(int max = 3)
    {
        try
        {
            if (!File.Exists(LogPath))
                return 0;

            string[] lines;
            using (var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var buffer = new List<string>(256);
                while (reader.ReadLine() is { } line)
                    buffer.Add(line);
                lines = buffer.ToArray();
            }

            var failures = 0;

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var kind = ReadKind(lines[i]);

                // 上一次是正常走完的，之前的历史再糟也与本次无关
                if (kind is "SHOWN" or "EXIT")
                    break;

                if (kind != "BEGIN")
                    continue;

                failures++;
                if (failures >= max)
                    break;
            }

            return failures;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 记一行。格式固定为「时间 | PID | 类型 | 详情」，
    /// 便于人工肉眼扫，也便于将来写脚本解析。
    /// </summary>
    private static void Write(string kind, string detail)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                RollIfTooLarge();

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {Environment.ProcessId} | {kind,-5} | {detail}{Environment.NewLine}";
                using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(line);
            }
        }
        catch
        {
            // 诊断设施不允许影响启动
        }
    }

    /// <summary>
    /// 单个文件超过上限就滚成 .old（覆盖旧的 .old）。
    /// 只留一份，因为这里只关心「最近几次」。
    /// </summary>
    private static void RollIfTooLarge()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes)
                return;

            var old = LogPath + ".old";
            if (File.Exists(old))
                File.Delete(old);

            File.Move(LogPath, old);
        }
        catch
        {
            // 滚不动就继续追加，反正不会因此崩
        }
    }

    private static string ReadKind(string line)
    {
        // 时间 | PID | 类型 | …
        var parts = line.Split('|');
        return parts.Length >= 3 ? parts[2].Trim() : string.Empty;
    }

    /// <summary>
    /// 本次运行的环境快照。原生库搜索目录单独列出来 —— 单文件自解压的那几个
    /// <c>%TEMP%\.net\…</c> 目录就在它里面，是排查「原生库缺失」的第一现场。
    /// </summary>
    private static string DescribeRun()
    {
        var sb = new StringBuilder();

        sb.Append("ver=").Append(SafeVersion());
        sb.Append(" os=").Append(SafeOs());
        sb.Append(" 64bit=").Append(Environment.Is64BitProcess);
        sb.Append(" singlefile=").Append(IsSingleFilePublished());
        sb.Append(" exe=").Append(Environment.ProcessPath ?? "?");

        var native = SafeNativeSearchDirs();
        if (!string.IsNullOrEmpty(native))
            sb.Append(" native=").Append(native);

        return sb.ToString();
    }

    /// <summary>
    /// 是不是「单文件」形态：单文件发布时程序集不再位于 exe 旁边
    /// （<c>Assembly.Location</c> 为空，或与 exe 目录不同）。
    /// </summary>
    private static bool IsSingleFilePublished()
    {
        try
        {
#pragma warning disable IL3000 // 单文件下 Location 恒为空 —— 这正是我们要探测的信号
            var location = typeof(StartupTrace).Assembly.Location;
#pragma warning restore IL3000
            return string.IsNullOrEmpty(location)
                   || !string.Equals(
                       Path.GetDirectoryName(location),
                       AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string SafeNativeSearchDirs()
    {
        try
        {
            return AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeVersion()
    {
        try { return App.GetDisplayVersion(); }
        catch { return "?"; }
    }

    private static string SafeOs()
    {
        try { return RuntimeInformationSafe(); }
        catch { return "?"; }
    }

    private static string RuntimeInformationSafe() =>
        System.Runtime.InteropServices.RuntimeInformation.OSDescription.Replace(' ', '_');
}
