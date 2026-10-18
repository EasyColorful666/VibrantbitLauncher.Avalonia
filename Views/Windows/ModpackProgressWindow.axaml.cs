using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Windows;

/// <summary>
/// 整合包安装的**进度详情页**。
///
/// <para>
/// 数据源是 <see cref="ModpackInstallSession"/>（通过 <c>DataContext</c> 传入），
/// 本窗口只做展示与三个动作：取消安装、打开实例目录、关闭。
/// 窗口可以随时关掉，安装会在后台继续（进度仍能在「下载任务」里看到）。
/// </para>
/// </summary>
public partial class ModpackProgressWindow : UserControl, Services.IInlinePage
{

    /// <summary>内嵌到主窗口时，顶栏显示的标题。</summary>
    public string InlineTitle => "安装进度";

    public ModpackProgressWindow()
    {
        InitializeComponent();


    }

    private ModpackInstallSession? Session => DataContext as ModpackInstallSession;

    /// <summary>
    /// 安装还在跑时离开这个页面：说明一下「后台继续」，免得用户以为关掉就是取消了。
    /// （1.0.8.0 起这里是内嵌页，没有了 Window.Closing 事件，改在「返回」时主动提示。）
    /// </summary>
    private void NotifyBackgroundContinue()
    {
        if (Session is { IsInProgress: true, IsCanceling: false })
        {
            AppServices.Notifications?.Show(
                "安装仍在后台继续，可在「下载任务」里查看进度",
                Session?.WindowTitle ?? "安装",
                NotificationSeverity.Informational);
        }
    }

    private async void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (Session is not { } session || !session.CanCancel)
            return;

        // 取消不可逆（已下好的文件会留着，但实例大概率不完整），先确认一下
        if (AppServices.Dialogs is { } dialogs)
        {
            var confirmed = await dialogs.ConfirmAsync(
                "取消安装",
                $"「{session.PackName}」还没装完。取消后已经下载的文件会保留，但实例可能不完整，需要重新安装。",
                "确定取消",
                "继续安装");

            if (!confirmed)
                return;
        }

        session.RequestCancel();
    }

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        var session = Session;
        if (session is null)
            return;

        try
        {
            var mcFolder = GameInstanceHelper.GetMinecraftFolder();
            if (string.IsNullOrWhiteSpace(mcFolder))
            {
                AppServices.Notifications?.Warning("还没有配置 .minecraft 目录");
                return;
            }

            // 与安装时的落点判定同源（见 ModpackInstaller.ResolveGameRoot）
            var dir = SettingsService.Current.VersionIsolation && !string.IsNullOrEmpty(session.InstanceId)
                ? Path.Combine(mcFolder, "versions", session.InstanceId)
                : mcFolder;

            if (!Directory.Exists(dir))
            {
                AppServices.Notifications?.Warning("实例目录还不存在，可能安装尚未走到创建实例这一步");
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "打开整合包实例目录失败");
            AppServices.Notifications?.Error($"打开目录失败：{ex.Message}");
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        NotifyBackgroundContinue();
        Services.InlineHost.Close();
    }
}
