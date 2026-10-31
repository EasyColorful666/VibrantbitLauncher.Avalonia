using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Serilog;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Views.Windows;

/// <summary>
/// 游戏内悬浮工具箱（Alt+Shift+T）。
///
/// <para>
/// 一块<b>半透明、置顶、无边框</b>的小窗，盖在游戏画面上，不抢任务栏位置。
/// 只做一件事：<b>快速联机</b> —— 生成 / 输入联机码，起停虚拟网络，
/// 让人不用切出去也能把「开房间 → 发码 → 好友进来」这条路走完。
/// </para>
///
/// <para>
/// <b>为什么只有这一页：</b>起初这里还挂过「读存档种子 / 画地形」和「把指令敲进游戏」两页。
/// 前者被证明是长期维护负担（每个大版本的存档布局都在变，而它只影响「看一眼」这一个动作），
/// 后者要在玩家的游戏窗口上做按键注入 —— 都是「收益配不上风险」的功能，已整体移除。
/// 少两页之后浮窗矮了一半，盖住的画面也更少。
/// </para>
///
/// <para>
/// <b>为什么不走 MVVM：</b>这个窗的东西全是「按一下就立刻做一件事」的命令式动作
/// （起进程、写剪贴板），没有需要持续观察的状态 —— 塞一层 ViewModel 只会多一层转发。
/// 与工程里其它工具窗（实例工具箱 / 补全资源 / 整合包进度）保持一致。
/// </para>
///
/// <para>
/// 它<b>常驻内存一次</b>：关闭走 <see cref="Window.Hide"/> 语义（由 ToolboxService 直接丢弃实例），
/// 每次唤出都是新建，避免上一轮的房间码残留。
/// </para>
/// </summary>
public partial class ToolboxWindow : Window
{
    /// <summary>浮窗离屏幕边缘的留白（DIP）。</summary>
    private const double ScreenMargin = 16;

    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };

    private bool _busy;

    /// <summary>
    /// 上一次启动联机失败的原因。
    ///
    /// 状态栏每 1.2 秒刷新一次，未运行时会被刷成「尚未开启虚拟网络」——
    /// 若不把错误记在这里，「启动失败」那一句会在下一次刷新时被吞掉，玩家只看到「没启动」。
    /// </summary>
    private string? _multiplayerError;

    public ToolboxWindow()
    {
        InitializeComponent();

        LoadHostCode();
        ApplyRole();
        UpdateSessionHeader();

        _statusTimer.Tick += (_, _) => RefreshMultiplayerStatus();

        Opened += OnOpened;
        Closed += OnClosed;
        KeyDown += OnKeyDown;
    }

    // ============================ 生命周期 ============================

    private void OnOpened(object? sender, EventArgs e)
    {
        PlaceAtTopRight();

        // 逐像素透明在部分老系统上不可用（没有 DWM 合成时）。那种情况下窗口是「什么都没画」，
        // 会看起来像一片黑或者干脆看不见 —— 退回不透明底色，至少内容还在。
        if (ActualTransparencyLevel == WindowTransparencyLevel.None)
        {
            if (this.TryFindResource("SurfaceCanvasBrush", ActualThemeVariant, out var brush) && brush is IBrush b)
                Shell.Background = b;
        }

        RefreshMultiplayerStatus();
        _statusTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
    }

    /// <summary>
    /// 默认停在屏幕**右上角**。
    ///
    /// <para>
    /// 选右上不是随手定的：游戏 HUD 里血条 / 快捷栏在正下方，小地图与状态类模组大多在左上，
    /// 右上角是遮挡最少的位置。偏移量按「工作区」而不是「整屏」算，任务栏在上面或左侧时也不会跑偏。
    /// </para>
    /// </summary>
    private void PlaceAtTopRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        var area = screen.WorkingArea;

        // Position 是物理像素，Width / Height 是 DIP，跨屏不同缩放时这一步不能省
        var width = (int)Math.Round(Bounds.Width > 0 ? Bounds.Width : Width, MidpointRounding.AwayFromZero);
        var height = (int)Math.Round(Bounds.Height > 0 ? Bounds.Height : Height, MidpointRounding.AwayFromZero);

        var margin = (int)Math.Round(ScreenMargin * scaling);
        var x = area.Right - (int)Math.Round(width * scaling) - margin;
        var y = area.Y + margin;

        // 屏幕比窗口还窄时不把窗口推出屏幕外，宁可贴左边
        Position = new PixelPoint(
            Math.Max(area.X + margin, x),
            Math.Max(area.Y + margin, y));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void DragBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 无边框窗口没有系统标题栏可拖，自绘这块负责「按住拖走」
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // ============================ 会话 ============================

    private static GameSession? Session => AppServices.GameSession?.Current;

    /// <summary>当前<b>实际生效</b>的唤出键位（可能因被占用而退到备选）。</summary>
    private static string HotkeyText => AppServices.Toolbox?.ActiveHotkeyText ?? ToolboxService.DefaultHotkeyText;

    private void UpdateSessionHeader()
    {
        var session = Session;

        InstanceText.Text = session is null
            ? "未检测到正在运行的游戏"
            : $"{session.InstanceId} · {session.DescribeLoader()} · 正在运行";

        // 热键只在游戏运行时才注册，所以底栏那句提示要跟着会话走 ——
        // 从托盘手动打开（没在玩游戏）时不能骗人说「按 X 呼出」。
        var hotkey = HotkeyText;
        HotkeyHint.Text = session is null
            ? $"游戏运行时按 {hotkey} 呼出"
            : $"{hotkey} 随时呼出";

        ToolTip.SetTip(CloseButton, $"关闭（{hotkey} 可再次唤出）");
    }

    // ============================ 快速联机 ============================

    private void LoadHostCode()
    {
        var saved = SettingsService.Current.EasyTierNetworkKey;

        if (RoomCode.TryParse(saved, out var code, out _) && code is not null)
        {
            HostCodeText.Text = code.Code;
            return;
        }

        var generated = RoomCode.Generate();
        HostCodeText.Text = generated;
        SettingsService.Current.EasyTierNetworkKey = generated;
        SettingsService.Save();
    }

    private bool IsHostSelected => HostRadio.IsChecked != false;

    private void Role_Changed(object? sender, RoutedEventArgs e) => ApplyRole();

    private void ApplyRole()
    {
        if (HostPanel is null || GuestPanel is null)
            return;

        var host = IsHostSelected;
        HostPanel.IsVisible = host;
        GuestPanel.IsVisible = !host;
    }

    private void Regenerate_Click(object? sender, RoutedEventArgs e)
    {
        var code = RoomCode.Generate();
        HostCodeText.Text = code;
        SettingsService.Current.EasyTierNetworkKey = code;
        SettingsService.Save();
        MultiplayerDetail.Text = "已换新码。旧码对应的房间不会再有人进来。";
    }

    private async void CopyCode_Click(object? sender, RoutedEventArgs e)
    {
        var text = IsHostSelected ? HostCodeText.Text : GuestCodeBox.Text;
        var ok = await CopyAsync(text);

        MultiplayerDetail.Text = ok
            ? "联机码已复制，发到群里等好友进来。"
            : "复制失败，请手动选中后复制。";
    }

    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = await Clipboard!.TryGetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
                GuestCodeBox.Text = text.Trim();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取剪贴板失败");
            MultiplayerDetail.Text = "读不到剪贴板内容，请手动粘贴。";
        }
    }

    private async void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var easyTier = AppServices.EasyTier;
        if (easyTier is null)
        {
            MultiplayerStatus.Text = "联机服务未就绪。";
            return;
        }

        var host = IsHostSelected;
        var raw = host ? HostCodeText.Text : GuestCodeBox.Text;

        if (string.IsNullOrWhiteSpace(raw))
        {
            MultiplayerStatus.Text = host ? "还没有房间码。" : "请先填入房主给你的联机码。";
            return;
        }

        if (!RoomCode.TryParse(raw, out var code, out var parseError) || code is null)
        {
            MultiplayerStatus.Text = parseError ?? "联机码无效。";
            return;
        }

        _busy = true;
        StartButton.IsEnabled = false;

        try
        {
            if (host)
            {
                HostCodeText.Text = code.Code;
                SettingsService.Current.EasyTierNetworkKey = code.Code;
                SettingsService.Save();
            }
            else
            {
                GuestCodeBox.Text = code.Code;
            }

            MultiplayerStatus.Text = "正在连接虚拟网络…";

            // 中继那一栏可能是「发地址的接口」而不是真节点，先把当下的真实地址取出来。
            // 取不到也照样往下走：核心自己有重试，我们这边取不到不代表它取不到。
            // 游戏里这一页没有中继设置项，直接沿用联机页的选择（含陶瓦兼容模式）。
            var settings = SettingsService.Current;
            var relay = settings.EasyTierTerracottaCompat
                ? RoomCode.TerracottaRelay
                : settings.EasyTierRelayServer;
            var chain = new List<string> { string.IsNullOrWhiteSpace(relay) ? RoomCode.DefaultRelay : relay };
            if (RoomCode.ExtraRelaysFor(chain[0]) is { } extras)
                chain.AddRange(extras);

            var relays = new List<string>(await PublicRelayResolver.ResolveAllAsync(chain));
            if (relays.Count == 0)
                relays.Add(chain[0]);

            // 先把上一次的失败原因清掉，否则万一这次成功、状态栏还会挂着旧错误
            _multiplayerError = null;

            var error = await easyTier.StartAsync(
                code.NetworkName,
                code.NetworkSecret,
                host,
                relays[0],
                SettingsService.Current.KeepMultiplayerAlive,
                relays.Skip(1).ToArray());

            if (error is not null)
            {
                _multiplayerError = error;
                MultiplayerDetail.Text = string.Empty;
            }
            else
            {
                // 下一步该做什么放 Detail 这行：状态行由定时器占用，写了也留不住
                MultiplayerDetail.Text = host
                    ? "下一步：进游戏「对局域网开放」，让好友在「多人游戏 → 直接连接」里填你的虚拟 IP。"
                    : "下一步：进游戏「多人游戏 → 直接连接」，填房主的虚拟 IP。";
            }
        }
        catch (Exception ex)
        {
            // 命令是 async void 派发过来的，异常漏出去会直接崩掉应用
            Log.Error(ex, "工具箱启动联机失败");
            _multiplayerError = $"启动联机出错：{ex.Message}";
        }
        finally
        {
            _busy = false;
            StartButton.IsEnabled = true;
            RefreshMultiplayerStatus();
        }
    }

    private void Stop_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _multiplayerError = null;
            AppServices.EasyTier?.Stop();
            MultiplayerDetail.Text = "已断开虚拟网络。";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "工具箱停止联机失败");
            _multiplayerError = $"断开联机出错：{ex.Message}";
        }

        RefreshMultiplayerStatus();
    }

    private void RefreshMultiplayerStatus()
    {
        var easyTier = AppServices.EasyTier;
        if (easyTier is null)
            return;

        var running = easyTier.IsRunning;
        StopButton.IsEnabled = running;

        if (!running)
        {
            MultiplayerStatus.Text = _multiplayerError ?? "尚未开启虚拟网络。";
            return;
        }

        var ip = easyTier.LocalVirtualIp;
        var role = easyTier.IsHost ? "房主" : "房客";
        MultiplayerStatus.Text = string.IsNullOrEmpty(ip)
            ? $"虚拟网络已启动（{role}），正在获取虚拟 IP…"
            : $"虚拟网络已启动（{role}）· 你的虚拟 IP：{ip}";
    }

    // ============================ 杂项 ============================

    private async Task<bool> CopyAsync(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
            return false;

        try
        {
            if (Clipboard is null)
                return false;

            await Clipboard.SetTextAsync(value);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "写剪贴板失败");
            return false;
        }
    }
}
