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

        // 按当前平台拼文件名：EasyTier 每个平台发一个 zip
        // （easytier-windows-x86_64 / -linux-aarch64 / -macos-x86_64 …），
        // 以前这里写死了 windows 包，Linux 上一装解压出来是 .exe，核心自然起不来。
        var assetName = EasyTierService.ReleaseAssetName;
        var zipPath = Path.Combine(vblDirectory, assetName);

        try
        {
            Directory.CreateDirectory(vblDirectory);

            statusText.Text = "正在下载 EasyTier 核心…";

            await DownloadWithProgressAsync(
                $"https://v4.gh-proxy.org/https://github.com/EasyTier/EasyTier/releases/download/v{EasyTierService.CoreVersion}/{assetName}",
                zipPath);

            // 先校验再解压：解压出来的核心会被以管理员权限启动，而下载要经过第三方加速代理。
            // 校验不通过就直接删掉，绝不把没核对过的可执行文件留在磁盘上。
            statusText.Text = "正在校验安装包…";

            var verifyError = await EasyTierService.VerifyPackageAsync(zipPath);

            if (verifyError is not null)
            {
                try { File.Delete(zipPath); } catch { /* 删不掉也不影响，下次下载会覆盖 */ }

                Serilog.Log.Warning("联机内核安装已中止：{Reason}", verifyError);
                throw new InvalidOperationException(verifyError);
            }

            statusText.Text = "正在解压…";
            ZipFile.ExtractToDirectory(zipPath, vblDirectory, Encoding.UTF8, true);
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            // zip 不带 Unix 权限位：Linux / macOS 上解压出来的核心默认不可执行，
            // 启动时会直接「Permission denied」。这里显式补 0755。
            if (!OperatingSystem.IsWindows())
            {
                foreach (var exe in new[]
                         {
                             EasyTierService.ResolveCoreExecutable(),
                             EasyTierService.ResolveCliExecutable(),
                         })
                {
                    if (exe is not null)
                        MakeExecutable(exe);
                }
            }

            statusText.Text = "安装完成";
            Services.InlineHost.Close();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "下载 / 解压 EasyTier 核心失败（{Asset}）", assetName);
            statusText.Text = $"安装失败：{ex.Message}";
            button1.IsEnabled = true;
        }
    }

    /// <summary>给解压出来的核心补上可执行位（zip 不保留 Unix 权限）。</summary>
    private static void MakeExecutable(string path)
    {
        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "设置可执行权限失败：{Path}", path);
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
