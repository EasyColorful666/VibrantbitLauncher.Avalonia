using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 启动阶段的失败上报：**不依赖 Serilog、不依赖 Avalonia**，只用一个最先可用的通道。
///
/// <para>
/// <b>为什么必须单独做一层</b>：日志系统是在 <c>App.OnFrameworkInitializationCompleted</c>
/// 里才初始化（要读设置、要建目录）。在此之前发生的任何异常，<c>Log.Fatal</c> 都打到了一个
/// 静默 logger 上 —— 结果是进程直接退出，磁盘上没有一行记录、界面上没有任何提示，
/// 用户看到的就是「双击没反应」（2026-10-19 用户反馈）。
/// </para>
///
/// <para>
/// 这里做两件事：
/// ① 把完整异常（含 OS / 架构 / 运行时版本）写到**一定可写**的位置；
/// ② 用 Win32 <c>MessageBoxW</c> 弹一个最小提示，告诉用户错误摘要和日志在哪。
/// 之所以不用 Avalonia 弹窗：能走到这里，通常说明 Avalonia 恰恰是没能起来的那一环。
/// </para>
/// </summary>
internal static class StartupFailureReporter
{
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_TOPMOST = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>
    /// 记录并提示一次启动失败。本方法自身**绝不抛异常** —— 它已经是最后一道防线了。
    /// </summary>
    /// <param name="ex">原始异常。</param>
    /// <param name="title">提示框标题。</param>
    public static void Report(Exception ex, string title)
    {
        try
        {
            var logPath = TryWriteLog(ex);
            ShowMessage(title, BuildSummary(ex, logPath));
        }
        catch
        {
            // 连上报都失败了，只能放弃 —— 绝不让它掩盖原始错误
        }
    }

    /// <summary>把异常写到第一个能写的目录，返回日志路径；全都写不了时返回 null。</summary>
    private static string? TryWriteLog(Exception ex)
    {
        var content = BuildLogText(ex);
        var fileName = $"startup-error-{DateTime.Now:yyyyMMdd-HHmmss}.log";

        foreach (var dir in CandidateDirs())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, fileName);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                return path;
            }
            catch
            {
                // 换下一个候选目录
            }
        }

        return null;
    }

    /// <summary>
    /// 候选写入目录，按「用户最容易找到 → 一定可写」排序。
    ///
    /// 不把 exe 目录放第一位是因为：启动器可能被放在 Program Files 等只读位置，
    /// 那种情况下写盘会失败 —— 而启动失败恰恰可能就是因为这个。
    /// </summary>
    private static string?[] CandidateDirs() => new[]
    {
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibrantbitLauncher", "logs"),
        AppContext.BaseDirectory,
        Path.GetTempPath(),
    };

    private static string BuildLogText(Exception ex)
    {
        var sb = new StringBuilder();

        sb.AppendLine("VibrantbitLauncher 启动失败");
        sb.AppendLine("========================");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"版本：{SafeVersion()}");
        sb.AppendLine($"系统：{SafeOSDescription()}");
        sb.AppendLine($"系统架构：{RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"进程架构：{RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"64 位进程：{Environment.Is64BitProcess}");
        sb.AppendLine($"运行时：{RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"程序目录：{AppContext.BaseDirectory}");
        sb.AppendLine();
        sb.AppendLine("异常：");
        sb.AppendLine(ex.ToString());

        return sb.ToString();
    }

    private static string BuildSummary(Exception ex, string? logPath)
    {
        var sb = new StringBuilder();

        // 只取最内层有意义的一句，避免把整段堆栈塞进消息框
        var message = ex.GetBaseException().Message;
        if (message.Length > 300)
            message = message[..300] + "…";

        sb.AppendLine("启动器启动失败。");
        sb.AppendLine();
        sb.AppendLine(message);
        sb.AppendLine();

        if (logPath is not null)
        {
            sb.AppendLine("完整日志已保存到：");
            sb.AppendLine(logPath);
        }
        else
        {
            sb.AppendLine("（无法写入日志文件，请检查磁盘权限）");
        }

        sb.AppendLine();
        sb.AppendLine($"系统：{SafeOSDescription()}（{RuntimeInformation.OSArchitecture}）");
        sb.Append($"运行时：{RuntimeInformation.FrameworkDescription}");

        return sb.ToString();
    }

    private static string SafeVersion()
    {
        try
        {
            return App.GetDisplayVersion();
        }
        catch
        {
            return "unknown";
        }
    }

    private static string SafeOSDescription()
    {
        try
        {
            return RuntimeInformation.OSDescription;
        }
        catch
        {
            return Environment.OSVersion.ToString();
        }
    }

    private static void ShowMessage(string title, string text)
    {
        try
        {
            // 非 Windows 上没有 user32，直接用 stderr（终端启动时能看到）
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine($"[{title}] {text}");
                return;
            }

            MessageBoxW(IntPtr.Zero, text, title, MB_OK | MB_ICONERROR | MB_TOPMOST);
        }
        catch
        {
            // 弹不出来也不算失败
        }
    }
}
