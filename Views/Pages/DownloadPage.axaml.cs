using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

/// <summary>
/// 原版游戏下载页：版本清单 + 版本类型筛选 + 关键词搜索（相关度重排）。
/// </summary>
public partial class DownloadPage : UserControl
{
    private readonly DownloadPageViewModel? _viewModel;
    private readonly ObservableCollection<McVersion> _shown = new();
    private readonly List<McVersion> _all = new();

    private bool _loadedOnce;

    /// <summary>
    /// **已生效**的搜索关键词。
    ///
    /// <para>
    /// 搜索框里的文字和「正在生效的关键词」是两件事：玩家打了一半还没点搜索时，
    /// 列表不应该跟着变；此时如果他改了筛选勾选，也不该把没提交的半截关键词带上。
    /// 所以视图永远只读这个字段，它只在「点搜索 / 按回车」时被写入。
    /// </para>
    /// </summary>
    private string _appliedKeyword = string.Empty;

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

        // 默认只勾「正式版」：玩家装的多半是正式版，快照 / 远古 / 愚人节要自己点开。
        // 必须在订阅事件之前赋值，否则会被当成一次用户操作白刷一遍视图。
        chipRelease.IsChecked = true;
        AttachFilterHandlers();

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

    /// <summary>四个筛选勾选框（多选，可任意叠加）。</summary>
    private CheckBox[] FilterChips() => new[]
    {
        chipRelease, chipSnapshot, chipLegacy, chipAprilFools,
    };

    private void AttachFilterHandlers()
    {
        foreach (var chip in FilterChips())
            chip.IsCheckedChanged += OnFilterChipChanged;
    }

    /// <summary>
    /// 勾选 / 取消勾选后的即时重排。
    /// 筛选是开关式的显式操作，压着不给反应会显得卡，所以不走任何延时。
    /// </summary>
    private void OnFilterChipChanged(object? sender, RoutedEventArgs e)
    {
        RefreshView();
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
    /// 刷新过滤视图：先按勾选的版本类型过滤，再按关键词筛 + 相关度重排。
    /// 计数文案与空状态也在这里一并更新，避免三处各算一遍。
    /// </summary>
    private void RefreshView()
    {
        var kinds = SelectedKinds();
        var keyword = _appliedKeyword;

        // 一个类型都没勾 = 什么都不显示（这比「全显示」更符合勾选框的直觉：
        // 玩家把它当筛选器，不是当排除器）
        var scoped = _all.Where(v => kinds.Contains(v.Kind)).ToList();

        IEnumerable<McVersion> result = scoped;

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            result = scoped
                .Where(v => VersionSearchMatcher.IsMatch(v.Version, keyword))
                .OrderByDescending(v => VersionSearchMatcher.Score(v.Version, keyword))
                .ThenBy(v => v.Index);
        }

        _shown.Clear();
        foreach (var v in result)
            _shown.Add(v);

        UpdateCountText(scoped.Count, kinds.Count == 0, keyword);
    }

    /// <summary>当前勾选的大类集合。</summary>
    private HashSet<McVersionKind> SelectedKinds()
    {
        var kinds = new HashSet<McVersionKind>();
        if (chipRelease.IsChecked == true) kinds.Add(McVersionKind.Release);
        if (chipSnapshot.IsChecked == true) kinds.Add(McVersionKind.Snapshot);
        if (chipLegacy.IsChecked == true) kinds.Add(McVersionKind.Legacy);
        if (chipAprilFools.IsChecked == true) kinds.Add(McVersionKind.AprilFools);
        return kinds;
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
                    Type = entry.Type,
                    // 分类只在这一处做：页面和筛选都读 Kind，不各自判 type。
                    // 拿不到 type 的条目由分类器兜底成「正式版」，不会被筛丢。
                    Kind = McVersionKindClassifier.Classify(entry.McVersion, entry.Type),
                    Index = index
                }).ToList();
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _all.Clear();
                _all.AddRange(allVersions);
                UpdateKindCounts();
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

    /// <summary>
    /// 点「搜索」按钮发起搜索。
    ///
    /// <para>
    /// 这里刻意**不做输入即搜**：版本清单有 900 多条，边打边筛会让列表在
    /// 玩家打字过程中反复重建，既抖又难看清结果；而版本号是短词（<c>1.21</c>、
    /// <c>25w45a</c>），一次输完再搜的成本几乎没有。
    /// </para>
    /// </summary>
    private void OnSearchClick(object? sender, RoutedEventArgs e) => ApplySearch();

    /// <summary>输入框里按回车 = 点搜索；按 Esc = 清空并恢复当前筛选范围。</summary>
    private void TextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplySearch();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            textBox.Text = string.Empty;
            ApplySearch();
        }
    }

    /// <summary>把输入框里的词提交为「生效关键词」，再重新排一次视图。</summary>
    private void ApplySearch()
    {
        var keyword = textBox.Text?.Trim() ?? string.Empty;
        _appliedKeyword = keyword;

        Serilog.Log.Information("下载页搜索：{Keyword}", keyword.Length == 0 ? "(空，显示全部)" : keyword);
        RefreshView();
    }

    /// <summary>
    /// 回填四个胶囊上的数量（各类版本的总数，不受搜索影响）。
    /// 数量让玩家在勾选前就知道「测试版有 700 多个」，避免勾完等一大串列表。
    /// </summary>
    private void UpdateKindCounts()
    {
        var release = 0;
        var snapshot = 0;
        var legacy = 0;
        var aprilFools = 0;

        foreach (var v in _all)
        {
            switch (v.Kind)
            {
                case McVersionKind.Snapshot: snapshot++; break;
                case McVersionKind.Legacy: legacy++; break;
                case McVersionKind.AprilFools: aprilFools++; break;
                default: release++; break;
            }
        }

        countRelease.Text = release.ToString();
        countSnapshot.Text = snapshot.ToString();
        countLegacy.Text = legacy.ToString();
        countAprilFools.Text = aprilFools.ToString();
    }

    /// <summary>
    /// 刷新搜索框右侧的计数文案与空状态。
    ///
    /// <para>
    /// 分母用 <paramref name="scopedTotal"/>（当前勾选范围内的数量）而不是全量 920：
    /// 只勾了正式版再搜「1.20」时，写「匹配 3 / 共 920」会让人以为漏了两百个结果，
    /// 写「匹配 3 / 103」才说得通。
    /// </para>
    /// </summary>
    private void UpdateCountText(int scopedTotal, bool noKindSelected, string? keyword)
    {
        if (countText == null)
            return;

        var total = _all.Count;
        var shown = _shown.Count;
        var searching = !string.IsNullOrWhiteSpace(keyword);

        if (searching)
            countText.Text = $"匹配 {shown} / {scopedTotal} 个版本";
        else if (shown == total)
            countText.Text = $"共 {total} 个版本";
        else
            countText.Text = $"显示 {shown} / 共 {total} 个版本";

        // 列表还没加载出来时不报「空」——那不是筛选造成的空，是还没到货
        emptyText.IsVisible = shown == 0 && total > 0;

        if (shown > 0 || total == 0)
            return;

        // 空状态分三种原因，文案要说清「下一步该做什么」
        emptyText.Text = noKindSelected
            ? "一个版本类型都没勾选\n点上面的多选框挑一种玩法吧"
            : searching
                ? "没有匹配的版本\n换个关键词，再点一次「搜索」"
                : "当前筛选条件下没有版本";
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
