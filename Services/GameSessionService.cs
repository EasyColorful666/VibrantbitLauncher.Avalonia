using System.Diagnostics;
using Serilog;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 一次「正在进行的游戏」：哪个实例、装在哪里、进程号是多少。
/// </summary>
public sealed class GameSession
{
    /// <summary>实例 id（<c>.minecraft/versions</c> 下的目录名）。</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>该实例所在的 .minecraft 目录（绝对路径）。</summary>
    public string McFolder { get; init; } = string.Empty;

    /// <summary>游戏进程号。</summary>
    public int ProcessId { get; init; }

    /// <summary>启动时刻。</summary>
    public DateTime StartedAt { get; init; } = DateTime.Now;

    /// <summary>给界面看的一行说明。</summary>
    public string DisplayName => $"{InstanceId}";

    /// <summary>加载器展示名（原版 / Fabric / Forge …）。由工具箱在需要时现算，避免启动路径上多读一次磁盘。</summary>
    public string DescribeLoader()
    {
        try
        {
            var kind = GameInstanceHelper.DetectLoader(Path.Combine(McFolder, "versions"), InstanceId);
            return ModLoaderHelper.DisplayNameOf(kind);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "识别实例 {Id} 的加载器失败", InstanceId);
            return "未知";
        }
    }
}

/// <summary>
/// 「现在有没有游戏在跑」的全局记账处。
///
/// <para>
/// 工具箱要解决的第一件事就是「玩家此刻在玩哪个实例」—— 联机要把房间码和这个实例对上，
/// 种子要读它的存档目录，快捷指令要送到它的窗口。这些东西散落在
/// <c>RunPageViewModel</c> 的局部变量里，页面一离开就没了，所以启动成功 / 进程退出时
/// 往这里登记一笔，其余模块只看这里。
/// </para>
///
/// <para>
/// 可以同时有多个实例在跑（启动器本来就允许），因此按进程号分组，
/// <see cref="Current"/> 取「最近启动且还活着」的那个 —— 也就是玩家最可能在看的窗口。
/// </para>
/// </summary>
public sealed class GameSessionService
{
    /// <summary>进程号 → 会话。</summary>
    private readonly Dictionary<int, GameSession> _sessions = new();

    private readonly object _gate = new();

    /// <summary>当前会话变化（启动 / 退出）时触发。可能在任意线程上，调用方自行切 UI 线程。</summary>
    public event Action? Changed;

    /// <summary>最近启动且仍在运行的那个会话；没有游戏在跑时为 null。</summary>
    public GameSession? Current
    {
        get
        {
            lock (_gate)
            {
                // 从新到旧挑第一个还活着的。每次都验一遍进程，避免「游戏崩了但退出事件没送到」
                // 导致工具箱一直认为游戏还在跑（那样热键会一直挂着，指令也送进一个空窗口）。
                foreach (var session in _sessions.Values.OrderByDescending(s => s.StartedAt).ToArray())
                {
                    if (IsAlive(session.ProcessId))
                        return session;

                    _sessions.Remove(session.ProcessId);
                }

                return null;
            }
        }
    }

    /// <summary>是否有游戏在跑。</summary>
    public bool IsRunning => Current is not null;

    /// <summary>
    /// 登记一次启动。进程秒退的情况由调用方在确认存活后再调用。
    /// </summary>
    public void MarkStarted(string instanceId, string mcFolder, int processId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || processId <= 0)
            return;

        lock (_gate)
        {
            _sessions[processId] = new GameSession
            {
                InstanceId = instanceId,
                McFolder = mcFolder,
                ProcessId = processId
            };
        }

        Log.Information("游戏会话登记：{Id}（PID {Pid}）", instanceId, processId);
        NotifyChanged();
    }

    /// <summary>登记一次退出。找不到对应进程号时静默忽略。</summary>
    public void MarkStopped(int processId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _sessions.Remove(processId);
        }

        if (!removed)
            return;

        Log.Information("游戏会话结束：PID {Pid}", processId);
        NotifyChanged();
    }

    /// <summary>
    /// 取游戏主窗口句柄（注入按键要用）。窗口还没建出来时返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    public IntPtr TryGetWindowHandle(out GameSession? session)
    {
        session = Current;
        if (session is null)
            return IntPtr.Zero;

        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            return process.MainWindowHandle;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "取游戏窗口句柄失败（PID {Pid}）", session.ProcessId);
            return IntPtr.Zero;
        }
    }

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // 通知失败绝不能反过来影响启动 / 退出路径
            Log.Warning(ex, "游戏会话变更通知出错");
        }
    }

    private static bool IsAlive(int processId)
    {
        if (processId <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
