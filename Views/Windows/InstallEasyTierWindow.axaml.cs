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

namespace VibrantbitLauncher.Views.Windows;

/// <summary>
/// 安装联机模块：下载 EasyTier 核心压缩包并解压到 VBL 目录。
/// </summary>
public partial class InstallEasyTierWindow : Window
{
    public InstallEasyTierWindow()
    {
        InitializeComponent();

        // 兜底：挂到主窗口上，CenterOwner 才会真的居中于主窗口
        Owner ??= AppLifetime.MainWindow;
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
            Close();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "下载 / 解压 EasyTier 核心失败");
            statusText.Text = $"安装失败：{ex.Message}";
            button1.IsEnabled = true;
        }
    }

    private async Task DownloadWithProgressAsync(string url, string savePath)
    {
        using var httpClient = new HttpClient();
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(savePath, FileMode.Create);

        var totalBytes = response.Content.Headers.ContentLength.GetValueOrDefault();
        var buffer = new byte[8192];
        long bytesRead = 0;
        int bytesReceived;

        while ((bytesReceived = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesReceived);
            bytesRead += bytesReceived;

            // 报告进度（服务器没给 Content-Length 时跳过，否则会算成 NaN）
            if (totalBytes > 0)
            {
                var percent = (double)bytesRead / totalBytes * 100;
                await Dispatcher.UIThread.InvokeAsync(() => progressBar.Value = percent);
            }
        }
    }
}
