using System;
using Avalonia;
using Serilog;

namespace VibrantbitLauncher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
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
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
