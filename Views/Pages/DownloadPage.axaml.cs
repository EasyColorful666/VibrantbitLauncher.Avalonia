using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

/// <summary>
/// 原版游戏下载页：版本清单 + 关键词搜索（相关度重排）。
/// </summary>
public partial class DownloadPage : UserControl
{
    private readonly DownloadPageViewModel? _viewModel;
    private readonly ObservableCollection<McVersion> _shown = new();
    private readonly List<McVersion> _all = new();

    private readonly DispatcherTimer _debounceTimer;
    private bool _loadedOnce;

    /// <summary>最近一次发起版本列表加载的时刻，用于自动刷新的节流。</summary>
    private DateTime _lastVersionsLoadUtc = DateTime.MinValue;

    /// <summary>是否正在拉版本列表（挡住同一时刻的重复请求）。</summary>
    private bool _versionsLoading;

    /// <summary>
    /// 自动刷新的最小间隔（毫秒）。
    /// 取 800ms：既能挡住「页面刚建好 / 刚加载完」紧跟着的重复请求，
    /// 又不会误伤「启动后 1.3 秒的刷新」。
    /// </summary>
    private const int RefreshThrottleMs = 800;

    public DownloadPage()
    {
        _viewModel = AppServices.Provider?.GetService(typeof(DownloadPageViewModel)) as DownloadPageViewModel;
        DataContext = _viewModel;
        InitializeComponent();

        // 用自管的过滤集合替代 WPF 的 ListCollectionView（Avalonia 无此类型）
        listBox.ItemsSource = _shown;

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            RefreshView();
        };

        Loaded += async (_, _) =>
        {
            if (_loadedOnce)
                return;

            _loadedOnce = true;
            await LoadMcVersionsAsync();
        };

        // 程序运行后 1.3 秒的启动刷新（主窗口广播）
        WeakReferenceMessenger.Default.Register<RefreshDownloadDataMessage>(
            this, (_, _) => RefreshVersions());
    }

    /// <summary>
    /// 重新拉取 Minecraft 版本列表。
    /// 触发点：启动后 1.3 秒的广播、切入下载中心、以及在下载中心里切回「原版游戏」分类。
    /// 带节流，避免「刚进入页面」和「启动广播」同时到达时白拉一遍。
    /// </summary>
    public void RefreshVersions()
    {
        if (_versionsLoading)
            return;

        if ((DateTime.UtcNow - _lastVersionsLoadUtc).TotalMilliseconds < RefreshThrottleMs)
            return;

        Serilog.Log.Information("下载页刷新：Minecraft 版本列表");
        _ = LoadMcVersionsAsync();
    }

    /// <summary>
    /// 刷新过滤视图：有搜索词时按相关度重排，无搜索词时恢复原始顺序（新版本在前）。
    /// 这里同时刷新计数文案，避免两处各算一遍。
    /// </summary>
    private void RefreshView()
    {
        var keyword = textBox.Text;
        IEnumerable<McVersion> result = _all;

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            result = _all
                .Where(v => VersionSearchMatcher.IsMatch(v.Version, keyword))
                .OrderByDescending(v => VersionSearchMatcher.Score(v.Version, keyword))
                .ThenBy(v => v.Index);
        }

        _shown.Clear();
        foreach (var v in result)
            _shown.Add(v);

        UpdateCountText();
    }

    /// <summary>
    /// 异步加载 Minecraft 版本列表：后台线程取数据，分批推回 UI。
    /// </summary>
    private async Task LoadMcVersionsAsync()
    {
        if (_versionsLoading)
            return;

        // 开头就打戳：这次加载「刚发生过」，紧随其后的自动刷新请求要被跳过
        _lastVersionsLoadUtc = DateTime.UtcNow;
        _versionsLoading = true;

        try
        {
            var allVersions = await Task.Run(async () =>
            {
                var entries = await MinecraftVersionCache.GetAsync();
                return entries.Select((entry, index) => new McVersion
                {
                    Version = entry.McVersion,
                    Date = entry.ReleaseTime.ToString("yyyy-MM-dd"),
                    Index = index
                }).ToList();
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _all.Clear();
                _all.AddRange(allVersions);
                RefreshView();
            });
        }
        catch (Exception ex)
        {
            // 这里以前直接弹 ex.Message，超时时就是那句英文
            // “The operation was canceled.”；统一走中文翻译。
            var reason = VibrantbitLauncher.Helpers.FriendlyError.Describe(ex);
            Serilog.Log.Warning(ex, "下载页：版本列表加载失败");
            await Dispatcher.UIThread.InvokeAsync(() => AppServices.Notifications?.Error(reason));
        }
        finally
        {
            _versionsLoading = false;
        }
    }

    private void TextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    /// <summary>
    /// 刷新搜索框右侧的计数文案。
    /// 无搜索词时显示「共 N 个版本」；有搜索词时显示「匹配 M / 共 N 个版本」。
    /// </summary>
    private void UpdateCountText()
    {
        if (countText == null)
            return;

        var total = _all.Count;
        var keyword = textBox.Text;

        if (string.IsNullOrWhiteSpace(keyword))
        {
            countText.Text = $"共 {total} 个版本";
            emptyText.IsVisible = false;
            return;
        }

        var matched = _shown.Count;
        countText.Text = $"匹配 {matched} / 共 {total} 个版本";
        emptyText.IsVisible = matched == 0 && total > 0;
    }

    /// <summary>
    /// 整行卡片可点击：点一下直接进入安装页并带上该版本号。
    /// </summary>
    private void OnVersionCardClick(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control { DataContext: McVersion version }
            && _viewModel?.DownloadCommand.CanExecute(version.Version) == true)
        {
            _viewModel.DownloadCommand.Execute(version.Version);
        }
    }
}
