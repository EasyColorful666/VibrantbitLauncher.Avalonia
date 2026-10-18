using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Pages;

public partial class AccountPage : UserControl
{
    /// <summary>单个元素的入场时长：短促、不拖沓，看得出「浮上来」就够了。</summary>
    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(220);

    private const int EntranceStaggerMs = 40;

    private readonly AccountPageViewModel? _viewModel;

    /// <summary>已经播过入场动画的列表容器，避免增删账户时整列重新闪一遍。</summary>
    private readonly HashSet<Control> _enteredContainers = new();

    /// <summary>Hero 与右栏只在本页首次出现时入场，集合变化时不再重播。</summary>
    private bool _blocksEntered;

    /// <summary>入场动画的取消令牌：页面卸载 / 重新播放入场时，把上一轮的延迟全部掐掉。</summary>
    private CancellationTokenSource? _entranceCts;

    public AccountPage()
    {
        _viewModel = AppServices.Provider?.GetService(typeof(AccountPageViewModel)) as AccountPageViewModel;
        DataContext = _viewModel;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (_viewModel != null && !_viewModel.IsLoaded)
            {
                _viewModel.Load();
                _viewModel.IsLoaded = true;
            }

            ScheduleEntrance();
        };

        // 账户增删后补一次入场。订阅放在构造函数里：
        // 页面每次导航回来都会再触发 Loaded，写在 Loaded 里会重复挂同一个处理器，
        // 越切越多，同一批容器被排队动画好几次。
        if (_viewModel != null)
            _viewModel.Users.CollectionChanged += OnUsersChanged;

        // 页面被移出视觉树（切换导航）时掐掉还在排队的入场动画，
        // 否则回来时会对着一批已经不可见的容器跑动画。
        DetachedFromVisualTree += (_, _) =>
        {
            CancelEntrance();
            _blocksEntered = false;
        };
    }

    private void OnUsersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 清空账户后再次进入，Hero 与右栏应当重新入场
        if (_viewModel?.Users.Count == 0)
        {
            _enteredContainers.Clear();
            _blocksEntered = false;
        }

        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            ScheduleEntrance();
    }

    // ==================== 入场动画 ====================

    private void CancelEntrance()
    {
        _entranceCts?.Cancel();
        _entranceCts?.Dispose();
        _entranceCts = null;
    }

    /// <summary>
    /// 安排一次入场动画。
    ///
    /// 必须延到 <see cref="DispatcherPriority.Loaded"/> 之后：集合刚变化时
    /// ListBox 还没生成容器，这一刻去取只会拿到空列表，动画就静默没了。
    /// </summary>
    private void ScheduleEntrance()
    {
        if (SettingsService.Current.PerformanceMode)
            return;

        Dispatcher.UIThread.Post(PlayEntrance, DispatcherPriority.Loaded);
    }

    private void PlayEntrance()
    {
        CancelEntrance();

        if (VisualRoot is null)
            return;

        var cts = new CancellationTokenSource();
        _entranceCts = cts;

        // 分三层入场：Hero（焦点先到位）→ 右栏（操作区）→ 列表逐行。
        // 只在这三处动，其余元素保持静止 —— 动效要有指向，不是全屏一起飞。
        if (!_blocksEntered)
        {
            _blocksEntered = true;
            _ = PlayOneAsync(heroBlock, 0, cts.Token, fromScale: 1d, offsetY: 8d);
            _ = PlayOneAsync(asidePanel, 80, cts.Token, fromScale: 1d, offsetY: 8d);
        }

        var index = 0;
        foreach (var container in listbox1.GetRealizedContainers())
        {
            // 已经播过的不再重复：新加一个账户时，旧行不该跟着重新闪一遍
            if (!_enteredContainers.Add(container))
                continue;

            _ = PlayOneAsync(container, 100 + index++ * EntranceStaggerMs, cts.Token, fromScale: 0.98d, offsetY: 6d);
        }
    }

    private static async Task PlayOneAsync(
        Visual visual,
        int delayMs,
        CancellationToken ct,
        double fromScale,
        double offsetY)
    {
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        // 从「下方几像素、略小、全透明」浮到终态。终态由 AnimationHelper 写死为
        // 1 / scale(1) / 位移 0，播完 FillMode.None 自动回落，不需要收尾赋值。
        await AnimationHelper.AnimateAsync(
            visual,
            fromOpacity: 0d,
            toOpacity: 1d,
            fromScale: fromScale,
            toScale: 1d,
            fromOffsetY: offsetY,
            toOffsetY: 0d,
            EntranceDuration,
            AnimationHelper.DefaultEasing,
            ct);
    }
}
