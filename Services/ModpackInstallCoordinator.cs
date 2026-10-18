using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Views.Windows;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 整合包安装的**共享入口**：拖入主窗口、下载页「安装整合包」两条路径都走这里，
/// 保证进度上报、任务中心登记、进度详情页、成功/失败提示的表现完全一致。
///
/// <para>
/// 真正干活的是 <see cref="ModpackInstaller"/>；这一层只负责「把安装过程接进 UI 生态」：
/// 建安装会话 → 开进度详情页 → 建下载任务 → 转发进度 → 完成后刷新实例列表 → 弹提示。
/// 打开窗口的代码放这里（而不是三个调用点各写一遍），是为了让两条入口的行为不会走偏。
/// </para>
/// </summary>
public static class ModpackInstallCoordinator
{
    /// <summary>全局防重入：整合包安装很重（下载一个完整的 Minecraft + 加载器 + 模组），
    /// 同时开两个只会互相抢带宽和磁盘，还可能出现同一实例 id 的竞态。</summary>
    private static bool _running;

    /// <summary>当前是否有整合包正在安装（供拖入时的提示用）。</summary>
    public static bool IsRunning => _running;

    /// <summary>当前打开的进度详情窗口（同一时刻只保留一个，重复打开会激活已存在的那个）。</summary>
    private static ModpackProgressWindow? _progressWindow;

    /// <summary>
    /// 安装一个整合包文件。
    /// </summary>
    /// <param name="filePath">.mrpack 或 .zip 路径。</param>
    /// <param name="sourceLabel">来源描述（「拖入的文件」/「下载中心」/「配装码」），只用于提示文案。</param>
    /// <param name="kind">
    /// 来源类型。配装码在内部也是打成 <c>.mrpack</c> 走这里，但任务列表、进度窗口、提示文案
    /// 都必须显示成「配装」而不是「整合包」，否则用户会以为自己下错了东西。
    /// </param>
    /// <returns>安装成功返回实例 id；失败返回 null（提示已弹出）。</returns>
    public static async Task<string?> InstallAsync(
        string filePath,
        string sourceLabel = "整合包",
        InstallKind kind = InstallKind.Modpack,
        CancellationToken ct = default)
    {
        var subject = kind == InstallKind.Loadout ? "配装" : "整合包";

        if (_running)
        {
            AppServices.Notifications?.Warning($"已有{subject}正在安装，请等它完成后再试");
            ShowProgressWindow(ModpackInstallSession.Current);
            return null;
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            AppServices.Notifications?.Error($"找不到{subject}文件，可能已被移动或删除");
            return null;
        }

        // ---------- 先解析清单，确认这是个能装的包（失败要当场说清楚，别等到下载到一半） ----------
        ModpackInstaller.ModpackManifest manifest;
        try
        {
            ct.ThrowIfCancellationRequested();
            manifest = await Task.Run(() => ModpackInstaller.ReadManifest(filePath), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 准备阶段（生成安装包 / 解析清单）就点了取消。此时还没有安装会话，
            // 没有令牌往下传，只能在这里收兵 —— 否则用户点了取消，安装却照常开始。
            AppServices.Notifications?.Warning($"已取消这次{subject}安装", "已取消");
            return null;
        }
        catch (ModpackException ex)
        {
            AppServices.Notifications?.Error(ex.Message, $"无法安装{subject}");
            return null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "解析{Subject}失败：{File}", subject, filePath);
            AppServices.Notifications?.Error($"解析{subject}失败：{ex.Message}", $"无法安装{subject}");
            return null;
        }

        // ---------- 告诉用户这包要什么（版本 + 加载器），让他先有个预期 ----------
        var loaderName = ModLoaderHelper.DisplayNameOf(manifest.Loader);
        var detail = manifest.Loader == ModLoaderKind.Vanilla
            ? $"Minecraft {manifest.MinecraftVersion}"
            : $"Minecraft {manifest.MinecraftVersion} + {loaderName} {manifest.LoaderVersion}".TrimEnd();

        var taskService = AppServices.Provider?.GetService(typeof(DownloadTaskService)) as DownloadTaskService;

        // ---------- 建安装会话 + 打开进度详情页 ----------
        // 会话是详情页的唯一数据源；没有它用户只能看到一个百分比，看不到「在装哪个文件、哪步失败了」。
        var session = ModpackInstallSession.Begin(manifest, sourceLabel, filePath, kind);
        ShowProgressWindow(session);

        // 准备阶段点的取消会落在这里：清单解析完、会话刚建好，但用户已经不想装了。
        // 立刻把会话令牌置为取消，让下面第一步 ThrowIfCancellationRequested 直接收兵，
        // 避免「点了取消却又开始下载」。
        if (ct.IsCancellationRequested)
            session.RequestCancel();

        _running = true;
        var task = taskService?.CreateTask(
            $"{(kind == InstallKind.Loadout ? "配装码" : "整合包")}：{manifest.Name}",
            kind == InstallKind.Loadout ? DownloadTaskType.LoadoutInstall : DownloadTaskType.GameInstall);
        if (task is not null)
            task.IsModpack = true;

        try
        {
            AppServices.Notifications?.Show(
                $"开始安装{subject}「{manifest.Name}」（{detail}），共 {manifest.Files.Count} 个待下载文件",
                sourceLabel,
                NotificationSeverity.Informational);

            var progress = new Progress<ModpackInstaller.ModpackProgress>(p =>
            {
                // 用 Progress<T> 捕捉 UI 上下文，回调自动回到 UI 线程
                session.Apply(p);
                taskService?.UpdateProgress(task, p.Percent, p.Speed, p.Step);
            });

            var result = await ModpackInstaller.InstallAsync(
                filePath,
                p => ((IProgress<ModpackInstaller.ModpackProgress>)progress).Report(p),
                session.Token);

            // ---------- 收尾提示 ----------
            session.FinishSuccess(result);

            if (result.Skipped.Count > 0)
            {
                taskService?.CompleteTask(task,
                    $"安装完成（有 {result.Skipped.Count} 个文件未成功：{string.Join("、", result.Skipped.Take(3))}…）");
                AppServices.Notifications?.Warning(
                    $"{subject}「{manifest.Name}」已装好，但有 {result.Skipped.Count} 个文件没能下载："
                    + string.Join("；", result.Skipped.Take(3))
                    + "。可在「下载中心」手动补齐。",
                    "安装完成（部分文件缺失）");
            }
            else
            {
                taskService?.CompleteTask(task, $"安装完成：{result.InstanceId}");
                AppServices.Notifications?.Success(
                    $"{subject}「{manifest.Name}」已安装为实例 {result.InstanceId}"
                    + $"（{result.DownloadedFiles} 个文件"
                    + (result.OverriddenFiles > 0 ? $" + {result.OverriddenFiles} 个随包文件" : string.Empty)
                    + "），可在「启动」页选择它开始游戏",
                    "安装完成");
            }

            if (result.Warnings.Count > 0)
                Serilog.Log.Information("{Subject}安装提示：{Warnings}", subject, string.Join(" | ", result.Warnings));

            return result.InstanceId;
        }
        catch (OperationCanceledException)
        {
            session.FinishCanceled();
            // 取消 ≠ 失败：任务中心要显示「已取消」，不能染成失败色
            taskService?.CancelTask(task, "已取消");
            AppServices.Notifications?.Warning($"{subject}「{manifest.Name}」安装已取消", "已取消");
            return null;
        }
        catch (Exception ex)
        {
            var message = DescribeError(ex, subject);
            Serilog.Log.Error(ex, "{Subject}安装失败：{Name}", subject, manifest.Name);
            session.FinishFailure(message);
            taskService?.FailTask(task, message);
            AppServices.Notifications?.Error(message, $"{subject}安装失败");
            return null;
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// 打开（或激活）进度详情窗口。安装过程中调用可随时把详情页调出来；
    /// 没有会话时给一句提示，避免点了按钮什么都不发生。
    /// </summary>
    public static void ShowCurrentProgress()
    {
        if (ModpackInstallSession.Current is null)
        {
            AppServices.Notifications?.Warning("还没有进行过安装");
            return;
        }

        ShowProgressWindow(ModpackInstallSession.Current);
    }

    /// <summary>把窗口打开（或激活）到 UI 线程上。同一时刻只保留一个窗口。</summary>
    private static void ShowProgressWindow(ModpackInstallSession? session)
    {
        if (session is null)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowProgressWindow(session));
            return;
        }

        try
        {
            if (_progressWindow is { } existing)
            {
                // 已经内嵌显示了，只把内容换成当前会话（1.0.8.0 起不再是独立窗口）
                existing.DataContext = session;
                return;
            }

            var window = new ModpackProgressWindow { DataContext = session };
            _progressWindow = window;

            // 内嵌到主窗口内容区（学 PCL2）。它是「长任务页」，用户关掉后安装继续，
            // 任务中心照样能看到进度 —— 与原来关窗不断任务的行为一致。
            _ = Services.InlineHost.ShowAsync(window, session.WindowTitle)
                .ContinueWith(_ =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_progressWindow, window))
                            _progressWindow = null;
                    });
                }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            // 开不出窗口不该连带把安装也搞挂：进度仍在任务中心可见
            Serilog.Log.Warning(ex, "打开整合包进度窗口失败");
        }
    }

    /// <summary>把异常翻成中文可操作文案（与其它下载路径同一套判断）。</summary>
    private static string DescribeError(Exception ex, string subject)
    {
        if (ex is AggregateException agg && agg.InnerException is not null)
            ex = agg.InnerException;

        if (ex is ModpackException or DownloadFailedException)
            return ex.Message;

        var fs = DownloadErrors.DescribeFileSystem(ex);
        if (fs is not null)
            return fs;

        if (ex is HttpRequestException { StatusCode: not null } h)
            return $"{subject}下载失败：服务器返回 {(int)h.StatusCode!.Value}，请稍后重试";

        if (ex is OperationCanceledException or TimeoutException)
            return $"{subject}安装超时，请检查网络（国内网络访问境外资源通常需要代理）后重试";

        if (ex is HttpRequestException)
            return $"{subject}安装时网络中断，请检查网络或代理后重试";

        return $"{subject}安装失败：{ex.Message}";
    }
}
