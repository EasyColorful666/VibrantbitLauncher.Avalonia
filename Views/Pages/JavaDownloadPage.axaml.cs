using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Serilog;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Pages;

/// <summary>
/// Java 运行环境下载页。
///
/// <para>
/// 数据全部来自 Adoptium 的公开 REST API（见 <see cref="JavaRuntimeService"/>）：
/// 先取「可用主版本」，再按选中主版本取「当前平台的 GA 发行版」，反序列化后直接绑定到列表。
/// </para>
///
/// <para>
/// 这里刻意不用 ViewModel：本页的交互是「一次性拉列表 + 一个下载任务」，
/// 没有跨页面共享状态，写在代码后置里比再引一层 VM 更直观（与 ModPage 的做法一致）。
/// </para>
/// </summary>
public partial class JavaDownloadPage : UserControl
{
    private readonly ObservableCollection<JavaReleaseItem> _releases = new();
    private readonly ObservableCollection<JavaInstallation> _installed = new();

    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>
    /// 主版本列表请求的代次。只有「最新这一代」的结果才允许写进 <see cref="_releases"/>。
    ///
    /// <para>
    /// 光取消上一个请求不够：请求可能已经拿到 JSON、正排队等 UI 线程，
    /// 这时取消已经不会让它抛异常了，旧数据照样会填回列表 ——
    /// 表现就是「切到 Java 17，列表里还是 Java 21 的内容」。
    /// </para>
    /// </summary>
    private int _releasesGeneration;

    /// <summary>正在进行的版本列表请求。切版本时取消，省下没人在等的流量。</summary>
    private CancellationTokenSource? _releasesCts;

    /// <summary>
    /// 刷新主版本列表时抑制 <see cref="MajorBox"/> 的 SelectionChanged。
    ///
    /// <para>
    /// 重建 ItemsSource + 设 SelectedItem 会触发一次 SelectionChanged，
    /// 而刷新按钮自己还要再明确加载一次 —— 不抑制就是同一个版本请求两遍
    /// （后果不只是浪费：两代的先后顺序由网络决定，列表会闪一下）。
    /// </para>
    /// </summary>
    private bool _suppressMajorReload;

    /// <summary>安装根目录（页面上的可编辑路径框）。</summary>
    private string RuntimeRoot =>
        string.IsNullOrWhiteSpace(PathBox.Text)
            ? JavaRuntimeService.ResolveRuntimeRoot()
            : PathBox.Text!;

    public JavaDownloadPage()
    {
        InitializeComponent();

        ReleaseList.ItemsSource = _releases;
        InstalledList.ItemsSource = _installed;

        PlatformText.Text = $"当前平台　{JavaRuntimeService.PlatformText}";

        // 回填上次选过的根目录：这个值必须持久化，
        // 否则用户装到 D:\Java 后，设置页 / 启动页（走 JavaHelper 的候选扫描）会看不到这些 JDK。
        PathBox.Text = JavaRuntimeService.ResolveRuntimeRoot();

        // Linux 上「安装」先走系统包管理器，装不上才落到本地目录 —— 没有自定义目录这回事，
        // 界面跟着变（macOS 仍可自选目录，那边走的是压缩包解压）
        if (OperatingSystem.IsLinux())
            ApplySystemInstallUi();

        // 失焦时落盘 + 重扫，让「换了目录」立刻反映到下方的已安装列表
        PathBox.LostFocus += (_, _) => PersistRuntimeRoot();

        RefreshInstalled();

        // 页面构造发生在 UI 线程；首次拉列表放后台，避免阻塞页面切换。
        _ = LoadMajorsAsync();
    }

    /// <summary>
    /// Linux 的界面调整：安装先走系统包管理器（apt / dnf / pacman…），
    /// 装不上会自动回退「下载官方压缩包解压到路径框里这个目录」。
    ///
    /// <para>
    /// 所以路径框保留、禁用、并且显示**回退时真正会用的目录**（1.0.30.1 修正）：
    /// 之前这里锁死 <c>/usr/lib/jvm</c>，可那个目录普通用户写不进去，
    /// 回退安装必然失败，用户看到的却是「路径没问题」。
    /// </para>
    /// </summary>
    private void ApplySystemInstallUi()
    {
        Header.Subtitle = "优先通过系统包管理器安装；装不上时自动下载官方压缩包，装到下面这个目录";
        ReleaseTitle.Text = "可安装的版本";

        PathBox.Text = JavaRuntimeService.ResolveRuntimeRoot();
        PathBox.IsEnabled = false;
        BrowseButton.IsVisible = false;
    }

    /// <summary>把路径框里的根目录写进配置。空 / 非法路径时回退默认值，避免存进去一个坏路径。</summary>
    private void PersistRuntimeRoot()
    {
        var text = PathBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            // 清空 = 恢复默认
            SettingsService.Current.JavaRuntimeRoot = string.Empty;
            SettingsService.Save();
            PathBox.Text = JavaRuntimeService.DefaultRuntimeRoot;
            return;
        }

        if (!string.Equals(SettingsService.Current.JavaRuntimeRoot, text, StringComparison.OrdinalIgnoreCase))
        {
            SettingsService.Current.JavaRuntimeRoot = text;
            SettingsService.Save();
            Log.Information("Java 安装根目录已设为：{Root}", text);
        }
    }

    // ==================== 列表加载 ====================

    private async Task LoadMajorsAsync()
    {
        try
        {
            var majors = await JavaRuntimeService.GetAvailableMajorsAsync().ConfigureAwait(true);
            var list = majors.ToList();

            // 默认选 21：现代整合包最常见的要求（1.20.5+ 基本都要 21）
            var preferred = list.Contains(21) ? 21 : list.FirstOrDefault();

            // 换掉 ItemsSource 会把 SelectedItem 顶成 null，再赋一次又会触发 SelectionChanged；
            // 这里统一由本方法收尾时显式加载一次，中间那次事件直接忽略。
            _suppressMajorReload = true;
            try
            {
                MajorBox.ItemsSource = list;
                if (list.Count > 0)
                    MajorBox.SelectedItem = preferred;
            }
            finally
            {
                _suppressMajorReload = false;
            }

            if (list.Count > 0)
                await LoadReleasesAsync(preferred).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载 Java 主版本失败");
            EmptyText.IsVisible = true;
            EmptyText.Text = "获取 Java 版本列表失败，请检查网络后点「刷新」重试";
        }
    }

    /// <summary>
    /// 拉某个主版本的发行版列表。
    ///
    /// <para>
    /// 同一时刻只认最后一次切换：新请求开始时旧请求被取消，且旧请求即便已经拿到数据、
    /// 只要代次过期就整批丢弃。两件事都要做 —— 只取消会让「已在排队等 UI 线程」的结果漏网，
    /// 只判代次则白等一遍超时。这正是「切了主版本、列表还停在上一个版本」的成因。
    /// </para>
    /// </summary>
    private async Task LoadReleasesAsync(int major)
    {
        var generation = ++_releasesGeneration;

        var stale = _releasesCts;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _releasesCts = cts;
        if (stale is not null)
        {
            try
            {
                stale.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 上一个请求先一步自己收尾了，没什么可取消的
            }

            stale.Dispose();
        }

        _releases.Clear();
        EmptyText.IsVisible = true;
        EmptyText.Text = $"正在获取 Java {major} 的版本…";

        try
        {
            var items = await JavaRuntimeService.GetReleasesAsync(major, cts.Token).ConfigureAwait(true);

            // 数据到齐再一次性填：中途往 ObservableCollection 里逐条 Add，
            // 会让用户看见「列表自己长出来一半」的样子。
            if (generation != _releasesGeneration)
                return;

            foreach (var item in items)
                _releases.Add(item);

            EmptyText.IsVisible = _releases.Count == 0;
            EmptyText.Text = _releases.Count == 0
                ? $"没有找到适用于 {JavaRuntimeService.PlatformText} 的 Java {major} 发行版"
                : string.Empty;
        }
        catch (OperationCanceledException)
        {
            // 切到别的版本了：下一代负责显示，这里什么都不用改
            if (generation != _releasesGeneration)
                return;

            // 没被取代却取消了 = 20 秒超时。以前只写日志，界面上永远停着「正在获取…」
            Log.Warning("获取 Java {Major} 版本列表超时", major);
            EmptyText.IsVisible = true;
            EmptyText.Text = "获取版本列表超时（Adoptium 接口响应太慢），请点「刷新」重试";
        }
        catch (Exception ex)
        {
            if (generation != _releasesGeneration)
                return;

            Log.Warning(ex, "获取 Java {Major} 版本列表失败", major);
            EmptyText.IsVisible = true;
            EmptyText.Text = "获取版本列表失败（Adoptium 接口不可达），请稍后点「刷新」重试";
        }
        finally
        {
            if (ReferenceEquals(_releasesCts, cts))
                _releasesCts = null;

            cts.Dispose();
        }
    }

    private void RefreshInstalled()
    {
        _installed.Clear();

        foreach (var item in JavaRuntimeService.ScanInstalled(RuntimeRoot))
            _installed.Add(item);

        NoInstalledText.IsVisible = _installed.Count == 0;
        InstalledList.IsVisible = _installed.Count > 0;
    }

    // ==================== 事件 ====================

    private void MajorBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressMajorReload)
            return;

        if (MajorBox.SelectedItem is int major)
            _ = LoadReleasesAsync(major);
    }

    private async void Reload_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy)
            return;

        try
        {
            ReloadButton.IsEnabled = false;

            // 主版本列表与当前版本的发行版列表由它一并刷新，这里不要再加载一遍 ——
            // 重复请求会让同一个版本的两批数据抢着写入，谁后到谁赢
            await LoadMajorsAsync().ConfigureAwait(true);
            RefreshInstalled();
        }
        finally
        {
            ReloadButton.IsEnabled = true;
        }
    }

    private async void Browse_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picked = await (AppServices.FilePicker?.PickFolderAsync("选择 Java 安装目录")
                            ?? Task.FromResult<string?>(null));

        if (!string.IsNullOrWhiteSpace(picked))
        {
            PathBox.Text = picked;
            RefreshInstalled();
        }
    }

    /// <summary>下载并安装某个版本。</summary>
    private async void Download_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (sender is not Button { Tag: JavaReleaseItem item })
            return;

        // 目录名与 Temurin 官方安装器保持一致（jdk-21.0.12.1-hotspot），
        // 这样别家工具按约定路径也能找到同一个 Java。
        // 先把路径框里的根目录落盘：用户可能直接点安装、输入框还没失焦过。
        PersistRuntimeRoot();

        var targetDir = System.IO.Path.Combine(
            RuntimeRoot, JavaRuntimeService.BuildInstallFolderName(item));

        _busy = true;
        _cts = new CancellationTokenSource();
        SetBusyUi(true);
        ShowProgress(true, $"准备下载 Java {item.Version}");

        try
        {
            var javaPath = await JavaRuntimeService.InstallAsync(
                item, targetDir,
                (percent, text) => Dispatcher.UIThread.Post(() =>
                {
                    ProgressBar.Value = percent;
                    StatusText.Text = text;
                }),
                _cts.Token).ConfigureAwait(true);

            ProgressBar.Value = 100;
            StatusText.Text = $"安装完成：{javaPath}";
            AppServices.Notifications?.Success($"Java {item.Version} 安装完成", "Java");

            // 用户还没配过 Java 时顺手填上，省得再去设置页找一遍
            if (string.IsNullOrWhiteSpace(SettingsService.Current.JavaPath))
            {
                SettingsService.Current.JavaPath = javaPath;
                SettingsService.Save();
                Log.Information("已把新装的 Java 设为默认：{Path}", javaPath);
            }

            RefreshInstalled();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消";
            Log.Information("用户取消了 Java 下载");
        }
        catch (Exception ex)
        {
            // 这里要求「可操作」：大多数失败是网络，其次是路径无权限
            var message = ex.Message;
            StatusText.Text = $"安装失败：{message}";
            Log.Error(ex, "Java 安装失败：{Version}", item.Version);
            AppServices.Notifications?.Error(message, "Java 安装失败");
        }
        finally
        {
            _busy = false;
            SetBusyUi(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 已经结束
        }
    }

    /// <summary>把某个已安装的 Java 设为启动游戏用的那个。</summary>
    private void UseAsLaunchJava_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: JavaInstallation item })
            return;

        SettingsService.Current.JavaPath = item.JavaPath;
        SettingsService.Save();

        RefreshInstalled();
        StatusText.Text = $"已设为启动 Java：{item.JavaPath}";
        AppServices.Notifications?.Success("已设为启动 Java", "Java");

        // 进度面板本来只在下载时显示，这里借用一下做反馈
        ShowProgress(true, StatusText.Text!);
    }

    private void OpenFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: JavaInstallation item })
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.Home,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开目录失败：{Path}", item.Home);
        }
    }

    // ==================== 界面状态 ====================

    private void SetBusyUi(bool busy)
    {
        ReloadButton.IsEnabled = !busy;
        MajorBox.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        ReleaseList.IsEnabled = !busy;
        InstalledList.IsEnabled = !busy;
    }

    private void ShowProgress(bool visible, string text)
    {
        ProgressPanel.IsVisible = visible;
        StatusText.Text = text;
        if (!visible)
            ProgressBar.Value = 0;
    }
}
