using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
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
            await Dispatcher.UIThread.InvokeAsync(() => AppServices.Notifications?.Error(ex.Message));
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
