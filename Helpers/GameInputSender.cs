using System.Runtime.InteropServices;
using Serilog;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 把一段文本「敲」进正在运行的游戏窗口。
///
/// <para>
/// 启动器没有、也不该有往游戏进程里注入代码的能力，所以「快捷指令」只有一条诚实的技术路径：
/// 让游戏窗口获得焦点，然后像玩家自己一样按键 —— 先按聊天键 <c>T</c> 打开输入框，
/// 再把指令当字符逐个敲进去，最后回车。
/// </para>
///
/// <para>
/// 字符一律用<b>Unicode 注入</b>（<c>KEYEVENTF_UNICODE</c>）而不是「虚拟键 + Shift」：
/// 后者要按当前键盘布局去猜哪个键能打出这个符号，中文输入法环境下几乎必错；
/// Unicode 注入直接把字符交给系统，与布局无关。
/// 但打开聊天这一步必须用<b>虚拟键</b> <c>T</c>：游戏监听的是按键回调（<c>WM_KEYDOWN</c>），
/// 不是字符回调，纯字符注入打不开聊天框。
/// </para>
///
/// <para>
/// 前置条件是「游戏窗口真的在前台」：跨进程抢前台会被系统拦下，所以这里先做一次
/// 线程输入队列附加（<c>AttachThreadInput</c>）再切，并<b>复核</b>切换结果 ——
/// 没切过去就宁可报错让玩家自己点一下，也绝不把一串指令敲到别人的窗口里。
/// </para>
/// </summary>
public static class GameInputSender
{
    private const int SwRestore = 9;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;

    /// <summary>虚拟键：T（Minecraft 默认的聊天键）。</summary>
    private const ushort VkT = 0x54;

    /// <summary>虚拟键：回车。</summary>
    private const ushort VkReturn = 0x0D;

    /// <summary>单次 <c>SendInput</c> 的最大条数，分批发避免被系统丢弃。</summary>
    private const int InputBatchSize = 64;

    /// <summary>这个窗口现在是不是前台窗口。</summary>
    public static bool IsForeground(IntPtr hwnd)
        => hwnd != IntPtr.Zero && IsWindow(hwnd) && GetForegroundWindow() == hwnd;

    /// <summary>
    /// 把游戏窗口切到前台。返回 false 表示没切过去（调用方应提示玩家手动点一下）。
    /// </summary>
    public static bool TryActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return false;

        if (GetForegroundWindow() == hwnd)
            return true;

        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);

        // 系统只允许「当前前台进程」或「刚收到用户输入的进程」抢前台。
        // 把自己的输入队列临时挂到目标线程上，能让这次 SetForegroundWindow 通过；
        // 用完必须摘掉，否则两个线程的键盘状态会互相污染。
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground != IntPtr.Zero
            ? GetWindowThreadProcessId(foreground, out _)
            : 0;
        var targetThread = GetWindowThreadProcessId(hwnd, out _);

        var attached = false;
        if (foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread)
            attached = AttachThreadInput(foregroundThread, targetThread, true);

        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(foregroundThread, targetThread, false);
        }

        // 给窗口一点时间响应激活，再复核结果
        Thread.Sleep(120);
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>
    /// 把一条指令送进游戏。返回 null 表示成功，否则是给玩家看的错误文本。
    /// </summary>
    /// <param name="command">指令正文，可以不带前导斜杠（会自动补）。</param>
    public static string? SendCommand(IntPtr hwnd, string command)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return "没找到游戏窗口。游戏可能还在加载，稍等一会儿再试。";

        var text = (command ?? string.Empty).Trim();
        if (text.Length == 0)
            return "指令是空的。";

        // 聊天框里发指令必须带斜杠，否则会被当成普通聊天内容喊出去
        if (!text.StartsWith('/'))
            text = "/" + text;

        if (!TryActivateWindow(hwnd))
            return "没能把游戏切到最前面。请先手动点一下游戏窗口，再回来点「送进游戏」。";

        // ① 打开聊天框
        SendKeys(BuildVirtualKey(VkT));

        // 游戏打开聊天框有一帧动画 + 焦点切换，太快打字会丢前几个字符
        Thread.Sleep(180);

        // ② 敲指令
        if (!SendKeys(BuildUnicode(text)))
            return "系统拒绝了按键注入，指令没能送进游戏。可以改用「复制指令」自己粘贴。";

        // ③ 回车执行
        Thread.Sleep(120);
        SendKeys(BuildVirtualKey(VkReturn));

        Log.Information("已向游戏窗口 {Hwnd} 注入指令：{Command}", hwnd, text);
        return null;
    }

    /// <summary>
    /// 只把文本放进游戏聊天框（不回车）。留作「想先看一眼再发」的用法。
    /// </summary>
    public static string? TypeIntoChat(IntPtr hwnd, string text)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return "没找到游戏窗口。";

        if (!TryActivateWindow(hwnd))
            return "没能把游戏切到最前面。请先手动点一下游戏窗口再试。";

        SendKeys(BuildVirtualKey(VkT));
        Thread.Sleep(180);
        return SendKeys(BuildUnicode(text)) ? null : "系统拒绝了按键注入。";
    }

    // ============================ 输入构造 ============================

    private static INPUT[] BuildVirtualKey(ushort vk)
        => new[]
        {
            new INPUT { type = InputKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } },
            new INPUT { type = InputKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KeyeventfKeyup } } }
        };

    private static INPUT[] BuildUnicode(string text)
    {
        var list = new List<INPUT>(text.Length * 2);

        foreach (var ch in text)
        {
            list.Add(new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KeyeventfUnicode } }
            });
            list.Add(new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KeyeventfUnicode | KeyeventfKeyup } }
            });
        }

        return list.ToArray();
    }

    private static bool SendKeys(INPUT[] inputs)
    {
        if (inputs.Length == 0)
            return true;

        var size = Marshal.SizeOf<INPUT>();

        for (var offset = 0; offset < inputs.Length; offset += InputBatchSize)
        {
            var count = Math.Min(InputBatchSize, inputs.Length - offset);
            var batch = new INPUT[count];
            Array.Copy(inputs, offset, batch, 0, count);

            var sent = SendInput((uint)count, batch, size);
            if (sent != count)
            {
                Log.Warning("按键注入不完整：期望 {Expected} 条，实际 {Actual} 条", count, sent);
                return false;
            }
        }

        return true;
    }

    // ============================ Win32 ============================

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
