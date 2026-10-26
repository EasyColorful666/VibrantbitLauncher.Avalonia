using System;
using Avalonia;
using Serilog;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 零、启动轨迹（2026-10-21）。
        //
        // 必须在**第一行**：用户报「另一台 Win10 上只能运行一次，之后双击毫无反应」。
        // 这种「毫无反应」有两种根因，且排查手段完全相反：
        //   · 宿主层失败（单文件自解压的原生库被隔离 / 被清理）→ 托管代码一行都没跑；
        //   · 托管层失败（渲染初始化崩溃）→ 跑了，但死在 UI 起来之前。
        // 这一行写下 BEGIN：有它 = 托管起来了，没它 = 宿主层就挂了。
        // 顺带把「上次连续失败几次」读出来，用于下面自动降级。
        StartupTrace.BeginAndClassify();

        // 兜底一：UI 线程之外的未处理异常（后台线程、终结器线程）。
        // 没有这一层的话，这类异常只会让进程凭空消失，用户看到的就是「闪退」。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                StartupTrace.MarkError("AppDomain: " + ex.GetType().Name + ": " + ex.Message);
                StartupFailureReporter.Report(ex, "启动器遇到未处理的错误");
                try { Log.Fatal(ex, "未处理异常（AppDomain）"); } catch { /* 日志可能还没起来 */ }
            }
        };

        try
        {
            // 一、单实例守卫（1.0.17.0）。
            //
            // 放在最前面：两个实例同时跑会一起读写 settings.json、往同一个 versions/ 里
            // 解压同一个版本、各起一份联机内核。这类故障事后极难解释，不如根本不让它发生。
            //
            // 第二个实例不弹「已在运行」的对话框 —— 它请已经在跑的那个把窗口调到最前
            // （含从托盘恢复）然后自己退出。用户看到的是窗口跳出来，而不是双击没反应。
            if (!SingleInstanceGuard.TryAcquireOrActivate())
            {
                // 记成「正常结束」而不是失败：否则连续双击几次会被 StartupTrace 数成
                // 「连续启动失败」，把下次启动自动降级成软件渲染（见 AppEnvironment）。
                StartupTrace.MarkExit("second-instance");
                Environment.ExitCode = 0;
                return;
            }

            // 最先探测运行环境：版本号探测结果会影响 AppBuilder 的渲染配置，
            // 必须赶在 BuildAvaloniaApp() 之前完成。
            AppEnvironment.Probe();

            // 性能模式决定渲染后端，而渲染后端在 AppBuilder 阶段就要定下来 ——
            // 此时 SettingsService 还没 Load（它依赖日志系统等），所以这里轻量地
            // 从 settings.json 里单独读一个字段。
            //
            // ⭐ 自动降级（2026-10-21）：上一次启动没能走到「窗口已显示」就死了，
            // 说明 GPU 那条路在这台机器上走不通（老驱动 ANGLE 崩溃、虚拟机没有 3D、
            // Win10 上反复 TDR 后驱动拉黑本进程）。这时候**本次直接改用软件渲染**，
            // 界面朴素但一定出得来。下一次成功启动后计数归零，又会自动回到 GPU ——
            // 所以它既是兜底也是自愈，用户不需要去设置里找开关。
            ForceSoftwareRendering = AppEnvironment.IsLowEndHardware
                                     || StartupTrace.LastFailures > 0
                                     || ReadPerformanceModeFromSettings()
                                     || HasFlag("--software-render")
                                     || string.Equals(
                                         Environment.GetEnvironmentVariable("VBL_SOFTWARE_RENDER"),
                                         "1",
                                         StringComparison.Ordinal);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // 兜底二：启动链上的异常。
            //
            // 顺序很重要：**先**用不依赖日志/UI 的通道把信息落到磁盘并弹窗，**再**写 Serilog。
            // 因为走到这里时 LogService 很可能还没初始化，Log.Fatal 打在一个静默 logger 上，
            // 以前的表现就是「双击没反应、什么都没有」（2026-10-19 用户反馈）。
            StartupTrace.MarkError("startup: " + ex.GetType().Name + ": " + ex.Message);
            StartupFailureReporter.Report(ex, "启动器启动失败");

            Log.Fatal(ex, "应用启动失败");
            Log.CloseAndFlush();

            // 不再 rethrow：WinExe 下重新抛出只会触发 WER 对话框（很多机器上被关闭），
            // 反而把我们自己的提示盖掉。这里设置退出码后正常走 finally。
            Environment.ExitCode = 1;
        }
        finally
        {
            // 先放掉「唯一实例」的位置，再收日志：这样关掉之后立刻再开不会被自己挡住。
            SingleInstanceGuard.Release();
            Log.CloseAndFlush();
        }
    }

    // 不调用 WithInterFont()：字体由 AppTheme.axaml 的 AppFont / AppDisplayFont 指定
    // （Segoe UI Variable Text / 微软雅黑 / HarmonyOS Sans），内置 Inter 只会白占约 1.9MB。
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        // Win32 渲染配置：只对 Windows 生效，其他平台传 null 走默认。
        var win32 = BuildWin32Options();
        if (win32 is not null)
            builder = builder.With(win32);

        return builder;
    }

    /// <summary>
    /// 组装 Win32 平台选项。
    ///
    /// 低配机器（Windows 7 / 32 位进程 / 核显）上最大的性能陷阱是 **GPU 渲染后端**：
    /// Avalonia 默认先试 D3D11，老显卡驱动上会失败并逐级回退到 D3D9 → OpenGL → 软件渲染，
    /// 每次回退都要重新初始化一次渲染上下文（肉眼可见的白屏等待），
    /// 而且如果驱动「半残」（D3D11 能建但画得极慢）反而比纯软件渲染更卡。
    ///
    /// 所以性能模式下直接固定成软件渲染：虽然 CPU 占用高些，但**行为确定**，
    /// 不会出现白屏 / 花屏 / 半死不活的掉帧，在老机器上是「稳定优先」的正确取舍。
    /// <c>UseCompositor</c> 保持 true —— 关掉它反而会退化到无合成器渲染，问题更多。
    ///
    /// ⚠️ 此时 Serilog 的 File sink 还没挂上（<c>LogService.Initialize</c> 在
    /// <c>OnFrameworkInitializationCompleted</c> 里才跑），所以这里只做决定、不写日志，
    /// 由启动链统一记录（见 App.axaml.cs 的「渲染后端」日志行）。
    /// </summary>
    private static Win32PlatformOptions? BuildWin32Options()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        if (!ForceSoftwareRendering)
            return null;

        return new Win32PlatformOptions
        {
            RenderingMode = new[] { Win32RenderingMode.Software },
        };
    }

    /// <summary>
    /// 从 settings.json 里单独读 <c>PerformanceMode</c>。
    ///
    /// 为什么不用 SettingsService.Load()：那条路径会连带初始化日志、规范路径、
    /// 触碰 MainWindowViewModel 的静态状态，在 AppBuilder 之前跑副作用太大。
    /// 这里只要一个布尔值，用 JsonDocument 直接挑出来最干净，
    /// 任何异常（文件不存在 / JSON 坏掉 / 字段缺失）都当作 false，绝不影响启动。
    /// </summary>
    private static bool ReadPerformanceModeFromSettings()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
            if (!System.IO.File.Exists(path))
                return false;

            var json = System.IO.File.ReadAllText(path).TrimStart('\uFEFF');
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            return doc.RootElement.TryGetProperty("PerformanceMode", out var el)
                   && el.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 是否强制软件渲染。在 <see cref="BuildAvaloniaApp"/> 阶段由环境变量 / 命令行决定；
    /// 启动链读到用户设置里的「性能模式」后也可以把它置为 true（仅用于日志说明）。
    /// </summary>
    public static bool ForceSoftwareRendering { get; set; }

    private static bool HasFlag(string flag)
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
