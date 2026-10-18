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
        try
        {
            // 最先探测运行环境：版本号探测结果会影响 AppBuilder 的渲染配置，
            // 必须赶在 BuildAvaloniaApp() 之前完成。
            AppEnvironment.Probe();

            // 性能模式决定渲染后端，而渲染后端在 AppBuilder 阶段就要定下来 ——
            // 此时 SettingsService 还没 Load（它依赖日志系统等），所以这里轻量地
            // 从 settings.json 里单独读一个字段。
            ForceSoftwareRendering = AppEnvironment.IsLowEndHardware
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
            Log.Fatal(ex, "应用启动失败");
            throw;
        }
        finally
        {
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
