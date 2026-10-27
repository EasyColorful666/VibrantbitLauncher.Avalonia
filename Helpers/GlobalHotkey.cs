using System.Runtime.InteropServices;
using Avalonia.Threading;
using Serilog;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 全局热键（系统级，游戏窗口在最前面时也能收到）。
///
/// <para>
/// Avalonia 没有跨平台的全局热键 API，这里直接走 Win32 <c>RegisterHotKey</c>。
/// 注册时把 <c>hWnd</c> 传 <see cref="IntPtr.Zero"/>，系统就会把 <c>WM_HOTKEY</c>
/// <b>投递到调用线程的消息队列</b> —— 于是不需要借主窗口的窗口句柄，也不用改 Avalonia 的
/// 消息处理（那样一旦框架升级就断），自己起一个带消息泵的线程足够。
/// </para>
///
/// <para>
/// <b>为什么必须是独立线程：</b><c>RegisterHotKey</c> / <c>UnregisterHotKey</c> 与消息泵
/// 必须在<b>同一个线程</b>上，否则注销不掉；把消息泵塞进 UI 线程会和 Avalonia 的循环抢消息。
/// </para>
///
/// <para>
/// 只支持 Windows；其它系统上 <see cref="TryRegister"/> 直接返回 false 并给出原因，
/// 调用方据此隐藏 / 禁用相关入口，而不是留一个按下去没反应的快捷键。
/// </para>
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    /// <summary>修饰键：Alt。</summary>
    public const uint ModAlt = 0x0001;

    /// <summary>修饰键：Ctrl。</summary>
    public const uint ModControl = 0x0002;

    /// <summary>修饰键：Shift。</summary>
    public const uint ModShift = 0x0004;

    /// <summary>修饰键：Win。</summary>
    public const uint ModWin = 0x0008;

    /// <summary>按住不放时只触发一次（否则长按会疯狂开合窗口）。</summary>
    private const uint ModNoRepeat = 0x4000;

    private const int WmHotkey = 0x0312;
    private const int WmQuit = 0x0012;

    /// <summary>同一进程里多个热键要互不相同的 id；取一个不撞的常量即可。</summary>
    private const int HotkeyId = 0x5642;

    private readonly uint _modifiers;
    private readonly uint _virtualKey;
    private readonly Action _callback;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private uint _threadId;
    private volatile bool _registered;
    private string? _error;
    private bool _disposed;

    /// <param name="modifiers">修饰键组合，用本类的 <c>Mod*</c> 常量按位或。</param>
    /// <param name="virtualKey">虚拟键码（如 <c>0x54</c> 是 T）。</param>
    /// <param name="callback">按下时执行；会自动切到 UI 线程。</param>
    public GlobalHotkey(uint modifiers, uint virtualKey, Action callback)
    {
        _modifiers = modifiers;
        _virtualKey = virtualKey;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    /// <summary>注册失败的原因；成功时为 null。</summary>
    public string? Error => _error;

    /// <summary>当前是否处于已注册状态。</summary>
    public bool IsRegistered => _registered;

    /// <summary>
    /// 注册并开始监听。返回 false 表示没注册上（原因见 <see cref="Error"/>）。
    /// </summary>
    public bool TryRegister()
    {
        if (_disposed)
            return false;

        if (!OperatingSystem.IsWindows())
        {
            _error = "全局热键目前只在 Windows 上可用。";
            return false;
        }

        if (_thread is not null)
            return _registered;

        // 这个信号量是复用的（同一实例可以「注册 → 注销 → 再注册」，比如一局游戏接一局），
        // 每次重新等之前必须清掉上一次留下的信号，否则会立刻返回、读到旧的 false。
        _ready.Reset();

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "VBL.GlobalHotkey"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 等线程把注册结果报回来（注册是同步且极快的，这里给 2 秒远够）
        _ready.Wait(TimeSpan.FromSeconds(2));

        if (!_registered)
        {
            Log.Warning("全局热键注册失败：{Reason}", _error ?? "未知原因");

            // 把线程收干净：下一次游戏开始时还能重新试一次
            // （占用它的那个软件可能已经被用户关掉了）。
            Unregister();
            return false;
        }

        return true;
    }

    private void Pump()
    {
        try
        {
            _threadId = GetCurrentThreadId();

            if (!RegisterHotKey(IntPtr.Zero, HotkeyId, _modifiers | ModNoRepeat, _virtualKey))
            {
                var code = Marshal.GetLastWin32Error();
                _error = code switch
                {
                    1409 => "这个快捷键已被其它程序占用，请先关掉占用它的软件。",
                    _ => $"系统拒绝了快捷键注册（错误码 {code}）。"
                };
                return;
            }

            _registered = true;
            _ready.Set();

            while (true)
            {
                // hWnd 传 0 = 取本线程的整条消息队列（窗口消息 + 线程消息）
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);

                // 0 = WM_QUIT；-1 = 出错。两种都退出循环。
                if (result is 0 or -1)
                    break;

                if (message.message == WmHotkey && message.wParam.ToInt32() == HotkeyId)
                {
                    // 回调里会开窗 / 切 UI，必须回到 UI 线程
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            _callback();
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "全局热键回调出错");
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _error ??= $"快捷键监听线程异常：{ex.Message}";
            Log.Error(ex, "全局热键监听线程异常");
        }
        finally
        {
            if (_registered)
            {
                try { UnregisterHotKey(IntPtr.Zero, HotkeyId); }
                catch (Exception ex) { Log.Debug(ex, "注销全局热键失败"); }

                _registered = false;
            }

            _ready.Set();
        }
    }

    /// <summary>注销（可以再次 <see cref="TryRegister"/>）。</summary>
    public void Unregister()
    {
        var thread = _thread;
        if (thread is null)
            return;

        _thread = null;

        // 投 WM_QUIT 让消息泵退出，由它自己在 finally 里注销 ——
        // 跨线程注销是无效的（系统按线程记录），只能这样收尾。
        if (_threadId != 0)
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);

        try { thread.Join(TimeSpan.FromSeconds(1)); }
        catch (Exception ex) { Log.Debug(ex, "等待快捷键线程退出失败"); }

        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Unregister();
        _ready.Dispose();
    }

    // ============================ Win32 ============================

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }
}
