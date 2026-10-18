using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Windows;

/// <summary>
/// 「补全资源」窗口：启动前体检发现缺东西时弹出来，把「缺什么」摆清楚，
/// 点一下只补缺的那部分，并显示进度。
///
/// <para>
/// 取代原来的做法 —— 启动流程里直接抛一句
/// 「缺少资源索引文件：…assets\indexes\5.json。说明该版本的资源没下载完整，
/// 请在「下载中心」重新安装该版本」然后弹个错误框。用户看到的是一条无路可走的报错，
/// 而实际上只缺几十 MB 甚至几个文件，重装整个版本是几百 MB。
/// </para>
///
/// <para>
/// 本窗口只做「展示 + 编排」，真正的下载在 <see cref="AssetRepairService"/> 里。
/// </para>
/// </summary>
public partial class AssetRepairWindow : UserControl, Services.IInlinePage
{

    /// <summary>内嵌到主窗口时，顶栏显示的标题。</summary>
    public string InlineTitle => "补全资源";

    private readonly IntegrityReport _report;

    private CancellationTokenSource? _cts;
    private bool _repairing;

    public AssetRepairWindow() : this(new IntegrityReport())
    {
        // 设计器 / XAML 加载器需要一个无参构造
    }

    public AssetRepairWindow(IntegrityReport report)
    {
        InitializeComponent();

        _report = report;

        IssueList.ItemsSource = report.Issues;

        SubtitleText.Text = string.IsNullOrEmpty(report.McVersion)
            ? "检测到当前实例的资源不完整"
            : $"实例 {report.McVersion} · 游戏目录 {report.McFolder}";
    }

    private async void OnRepairClick(object? sender, RoutedEventArgs e)
    {
        if (_repairing)
            return;

        _repairing = true;
        RepairButton.IsEnabled = false;
        RepairButton.Content = "正在补全…";
        CloseButton.IsEnabled = false;

        RepairProgress.Value = 0;
        SpeedText.Text = string.Empty;
        StatusText.Text = "准备补全资源";

        _cts = new CancellationTokenSource();

        try
        {
            var message = await AssetRepairService.RepairAsync(_report, UpdateProgress, _cts.Token);

            // 补完再体检一次：以磁盘上的真实结果为准，而不是「下载器说成功了」。
            var after = await Task.Run(() => InstanceIntegrityChecker.Check(_report.McFolder, _report.McVersion));

            if (after.IsHealthy)
            {
                RepairProgress.Value = 100;
                StatusText.Text = message + "（重新体检：资源完整）";
                RepairButton.Content = "已补全";
                RepairButton.IsEnabled = false;
            }
            else
            {
                // 还有缺的（多半是网络断了 / 某些文件 404）：把清单换成最新的一份，让用户再点一次。
                IssueList.ItemsSource = after.Issues;
                StatusText.Text = $"补全未完全成功：{after.Summary()}，可再点一次「开始补全」重试";
                RepairButton.Content = "重试";
                RepairButton.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "补全已取消";
            RepairButton.Content = "开始补全";
            RepairButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "补全资源失败：{Version}", _report.McVersion);
            StatusText.Text = "补全失败：" + Helpers.FriendlyError.Describe(ex);
            RepairButton.Content = "重试";
            RepairButton.IsEnabled = true;
        }
        finally
        {
            _repairing = false;
            CloseButton.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>进度回调（可能来自线程池），统一切回 UI 线程。</summary>
    private void UpdateProgress(int percent, string step, string speed)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RepairProgress.Value = Math.Clamp(percent, 0, 100);
            StatusText.Text = string.IsNullOrEmpty(step) ? StatusText.Text : step;
            SpeedText.Text = speed ?? string.Empty;
        });
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        // 正在补全时不让关：VanillaDownloader 没有外部取消令牌，
        // 中途关窗会让下载在后台继续跑、进度却没人看。
        if (_repairing)
            return;

        Services.InlineHost.Close();
    }
}
