using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.Services.Download;

namespace VibrantbitLauncher.Views.Windows;

/// <summary>
/// 安装联机模块：下载 EasyTier 核心压缩包并解压到 VBL 目录。
/// </summary>
public partial class InstallEasyTierWindow : UserControl, Services.IInlinePage
{

    /// <summary>内嵌到主窗口时，顶栏显示的标题。</summary>
    public string InlineTitle => "安装联机模块";

    public InstallEasyTierWindow()
    {
        InitializeComponent();

    }

    private async void Button_Click(object? sender, RoutedEventArgs e)
    {
        button1.IsEnabled = false;

        // 和 EasyTierService 用同一个基准目录，避免安装到这里、启动时去那里找。
        var vblDirectory = EasyTierService.VblDirectory;
        var zipPath = Path.Combine(vblDirectory, "easytier-windows-x86_64-v2.6.4.zip");

        try
        {
            Directory.CreateDirectory(vblDirectory);

            statusText.Text = "正在下载 EasyTier 核心…";

            await DownloadWithProgressAsync(
                "https://v4.gh-proxy.org/https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-windows-x86_64-v2.6.4.zip",
                zipPath);

            statusText.Text = "正在解压…";
            ZipFile.ExtractToDirectory(zipPath, vblDirectory, Encoding.UTF8, true);

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            statusText.Text = "安装完成";
            Services.InlineHost.Close();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "下载 / 解压 EasyTier 核心失败");
            statusText.Text = $"安装失败：{ex.Message}";
            button1.IsEnabled = true;
        }
    }

    /// <summary>
    /// 下载 EasyTier 核心包。
    ///
    /// 1.0.7.15 起走统一下载内核：镜像走的是 gh-proxy，单连接经常只有几百 KB/s，
    /// 分块并行后能明显快一截；进度也由内核按「已收字节 / 总字节」精确给出。
    /// </summary>
    private async Task DownloadWithProgressAsync(string url, string savePath)
    {
        var item = new DownloadItem(url, savePath, DisplayName: Path.GetFileName(savePath));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastReportMs = 0L;

        await FileDownloader.DownloadAsync(item, onProgress: (received, total) =>
        {
            if (total <= 0)
                return;

            if (clock.ElapsedMilliseconds - lastReportMs < 120)
                return;
            lastReportMs = clock.ElapsedMilliseconds;

            var percent = Math.Clamp(received * 100d / total, 0, 100);
            Dispatcher.UIThread.Post(() => progressBar.Value = percent);
        });
    }
}
