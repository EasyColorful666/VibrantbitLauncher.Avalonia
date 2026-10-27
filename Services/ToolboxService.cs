using Avalonia.Threading;
using Serilog;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Views.Windows;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 游戏内工具箱的开关与热键管理。
///
/// <para>
/// 热键只在<b>游戏运行期间</b>注册（首选 <c>Alt+Shift+T</c>）。理由很实在：全局热键会吃掉整个系统的
/// 这个组合，没在玩游戏时挂着它没有任何意义，还会跟别的软件抢。游戏退出即注销。
/// </para>
///
/// <para>
/// 首选组合被别的软件占掉是常态（QQ 的 <c>Ctrl+Alt+T</c> / <c>Ctrl+Alt+Z</c> / <c>Ctrl+Alt+A</c>
/// 一批都占），所以按 <see cref="HotkeyCandidates"/> 的顺序往下试，谁先注册成功谁生效。
/// 当前生效的键位会写进 <see cref="ActiveHotkeyText"/>，界面与通知都读它，
/// 绝不出现「提示说按这个键、实际却监听另一个键」。
/// </para>
///
/// <para>
/// 窗口只保留一个实例；再按一次热键是「关掉」而不是「再来一个」——
/// 否则玩家连按两下会堆出一叠一模一样的浮窗。
/// </para>
/// </summary>
public sealed class ToolboxService : IDisposable
{
    /// <summary>
    /// 候选热键，按优先级排列。第一个是玩家看到的默认键位，
    /// 后面的是「被占用时」的退路 —— 宁可换个组合能按出来，也不要按了没反应。
    /// </summary>
    private static readonly (uint Modifiers, uint VirtualKey, string Text)[] HotkeyCandidates =
    {
        (GlobalHotkey.ModAlt | GlobalHotkey.ModShift, 0x54, "Alt+Shift+T"),
        (GlobalHotkey.ModControl | GlobalHotkey.ModAlt, 0x54, "Ctrl+Alt+T"),
    };

    private readonly GameSessionService _sessions;

    private GlobalHotkey? _hotkey;
    private ToolboxWindow? _window;
    private bool _started;
    private bool _disposed;

    /// <summary>热键注册失败的提示只发一次，免得每次进游戏都弹。</summary>
    private bool _warnedAboutHotkey;

    public ToolboxService(GameSessionService sessions)
    {
        _sessions = sessions;
    }

    /// <summary>默认（首选）键位文案，用于还没注册过时的展示。</summary>
    public static string DefaultHotkeyText => HotkeyCandidates[0].Text;

    /// <summary>当前<b>实际生效</b>的键位文案；一次都没注册成功时是首选键位。</summary>
    public string ActiveHotkeyText { get; private set; } = DefaultHotkeyText;

    /// <summary>热键此刻是否真的在监听。</summary>
    public bool IsHotkeyActive => _hotkey?.IsRegistered == true;

    /// <summary>开始跟随游戏会话注册 / 注销热键。必须在 UI 线程调用一次。</summary>
    public void Start()
    {
        if (_started || _disposed)
            return;

        _started = true;
        _sessions.Changed += OnSessionChanged;

        // 启动时就已有游戏在跑（比如「启动后关闭启动器」又被同实例重开）：补一次注册
        SyncWithSession();
    }

    /// <summary>
    /// 打开工具箱（托盘菜单 / 热键共用）。已打开时改为关闭。
    /// </summary>
    public void Toggle()
    {
        if (_disposed)
            return;

        if (_window is not null)
        {
            CloseWindow();
            return;
        }

        ShowWindow();
    }

    /// <summary>无条件打开（菜单里明确写「打开工具窗」时用）。</summary>
    public void Show()
    {
        if (_disposed)
            return;

        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        ShowWindow();
    }

    private void ShowWindow()
    {
        try
        {
            var window = new ToolboxWindow();
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_window, window))
                    _window = null;
            };

            _window = window;
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开 Minecraft 工具箱失败");
            _window = null;
            AppServices.Notifications?.Show($"打不开工具箱：{ex.Message}", "工具箱", NotificationSeverity.Error);
        }
    }

    private void CloseWindow()
    {
        try
        {
            _window?.Close();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "关闭工具箱窗口失败");
        }
        finally
        {
            _window = null;
        }
    }

    private void OnSessionChanged()
    {
        // 会话变更可能在启动线程 / 进程退出回调线程上触发，一律切回 UI 线程处理
        Dispatcher.UIThread.Post(SyncWithSession);
    }

    /// <summary>按「当前有没有游戏在跑」注册或注销热键。</summary>
    private void SyncWithSession()
    {
        if (_disposed)
            return;

        var running = _sessions.Current is not null;

        if (running)
        {
            if (_hotkey is { IsRegistered: true })
                return;

            TryRegisterAny(out var error);

            if (_hotkey is { IsRegistered: true })
            {
                Log.Information("游戏已启动：工具箱热键 {Hotkey} 开始监听", ActiveHotkeyText);
                _warnedAboutHotkey = false;
            }
            else if (!_warnedAboutHotkey)
            {
                _warnedAboutHotkey = true;
                AppServices.Notifications?.Show(
                    $"游戏运行时按 {DefaultHotkeyText} 可以唤出工具箱，但这几个组合键都被别的软件占用了" +
                    $"（{error}）。可以先从托盘菜单打开工具箱，或者关掉占用它的软件。",
                    "快捷键不可用",
                    NotificationSeverity.Warning);
            }

            return;
        }

        if (_hotkey is { IsRegistered: true })
        {
            _hotkey.Unregister();
            Log.Information("游戏已退出：工具箱热键 {Hotkey} 停止监听", ActiveHotkeyText);
        }

        // 游戏没了就把浮窗也收掉 —— 留一个盖在桌面上的半透明窗只会让人困惑
        CloseWindow();
    }

    /// <summary>
    /// 按候选顺序尝试注册，第一个成功的就是本局生效的键位。
    /// 失败时会把最后一个失败原因带出去供提示。
    /// </summary>
    private void TryRegisterAny(out string? error)
    {
        error = null;

        // 上一局的实例要先彻底收掉（它内部是一条消息泵线程），只 Unregister 会留下空转的线程
        _hotkey?.Dispose();
        _hotkey = null;

        foreach (var candidate in HotkeyCandidates)
        {
            var hotkey = new GlobalHotkey(candidate.Modifiers, candidate.VirtualKey, Toggle);

            if (hotkey.TryRegister())
            {
                _hotkey = hotkey;
                ActiveHotkeyText = candidate.Text;

                if (!string.Equals(candidate.Text, DefaultHotkeyText, StringComparison.Ordinal))
                {
                    Log.Warning("{Primary} 已被占用，工具箱改用 {Fallback}", DefaultHotkeyText, candidate.Text);

                    AppServices.Notifications?.Show(
                        $"{DefaultHotkeyText} 被别的软件占用了，本局游戏改用 {candidate.Text} 唤出工具箱。",
                        "快捷键已调整",
                        NotificationSeverity.Informational);
                }

                return;
            }

            error = hotkey.Error;
            hotkey.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _sessions.Changed -= OnSessionChanged;

        CloseWindow();

        _hotkey?.Dispose();
        _hotkey = null;
    }
}
