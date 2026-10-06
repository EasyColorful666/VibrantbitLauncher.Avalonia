using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher;

/// <summary>临时冒烟：环境变量 VBL_SMOKE=1 时逐页导航并截图。验证完删除本文件。</summary>
internal static class SmokeTest
{
    public static async void Run(Window window)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "vbl-smoke");
            Directory.CreateDirectory(dir);

            await Task.Delay(1600);

            // 只列**顶层导航目标**：DownloadPage / ModPage 是 DownloadHubPage 内嵌的子页，
            // 对它们做顶层导航会因为「已有视觉父级」抛异常（这是本就存在的约束，不是本次改动引入的）
            var pages = new[]
            {
                "LaunchPage", "AccountPage", "DownloadHubPage", "DownloadCenterPage",
                "MultiplayerPage", "SettingsPage", "VersionManagePage", "DashboardPage"
            };

            foreach (var name in pages)
            {
                var type = typeof(App).Assembly.GetType($"VibrantbitLauncher.Views.Pages.{name}");
                if (type is null)
                {
                    Serilog.Log.Warning("[SMOKE] 缺页面 {Name}", name);
                    continue;
                }

                var page = AppServices.Provider?.GetService(type);
                if (page is null)
                {
                    Serilog.Log.Warning("[SMOKE] 解析不到 {Name}", name);
                    continue;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    Serilog.Log.Information("[SMOKE] navigate -> {Name}", name);
                    AppNavigation.Navigate(page);
                });

                // 过渡中途来一张，看交叉帧
                await Task.Delay(130);
                Capture(window, dir, name + "_mid");

                // 落定后再来一张，看最终状态
                await Task.Delay(900);
                Capture(window, dir, name + "_end");
            }

            Serilog.Log.Information("[SMOKE] 完成，输出 {Dir}", dir);
            await Task.Delay(300);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[SMOKE] 冒烟失败");
            Environment.Exit(2);
        }
    }

    private static void Capture(Window window, string dir, string name)
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var w = (int)Math.Max(1, window.ClientSize.Width);
                    var h = (int)Math.Max(1, window.ClientSize.Height);
                    using var bitmap = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(dir, name + ".png"));
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "[SMOKE] 截图失败 {Name}", name);
                }
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[SMOKE] 调度截图失败 {Name}", name);
        }
    }
}
