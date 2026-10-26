using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 单实例守卫：同一份安装只允许一个启动器进程在跑。
///
/// <para>
/// <b>为什么必须有</b>：启动器把配置写在 exe 旁边的 <c>settings.json</c>、把游戏文件写在
/// 用户选的那个游戏目录里、还要起联机内核和系统托盘。开两份就是两份同时读写同一批文件：
/// 后保存的覆盖先保存的配置、两边同时往同一个 <c>versions/</c> 下解压同一个版本、
/// 起两个 EasyTier 内核抢同一张虚拟网卡。表现出来是「设置改了又变回去」「版本装坏了」
/// 这类没法用一句话解释的故障。
/// </para>
///
/// <para>
/// <b>识别口径是「一份安装」而不是「一台机器」</b>：名字由 exe 所在目录算哈希。
/// 玩家把启动器复制一份到别的目录（各带自己的 settings.json），那是两个独立安装，
/// 互不影响，允许同时开 —— 强行禁止只会让「便携版 + 正式版共存」的人没法用。
/// </para>
///
/// <para>
/// <b>第二个实例不是「双击没反应」</b>：它会通过命名管道请已经在跑的那个把窗口调到最前
/// （包括从托盘恢复），然后自己退出。所以用户看到的是「窗口跳出来了」，
/// 而不是「点了没动静」—— 后者正是 2026-10-21 那次「只能运行一次」误判的观感来源。
/// </para>
///
/// <para>
/// 本类的入口方法<b>绝不因为自身出错而拦住启动</b>：拿不到互斥体就当成主实例继续跑
/// （宁可多开一个，也不能一次都开不起来）。
/// </para>
/// </summary>
internal static class SingleInstanceGuard
{
    /// <summary>第二个实例发给主实例的请求：把窗口叫到前面来。</summary>
    private const string ActivateMessage = "ACTIVATE";

    /// <summary>主实例的应答：收到了。没收到应答就当这次激活失败，但仍会退出本进程。</summary>
    private const string AckMessage = "RUNNING";

    /// <summary>等主实例应答的总时长。它只在启动最初几百毫秒里没建好管道，正常是毫秒级。</summary>
    private static readonly TimeSpan ActivateTimeout = TimeSpan.FromSeconds(2.5);

    private static readonly object Gate = new();

    /// <summary>主实例持有的互斥体。必须存活到进程结束，否则「唯一性」就没了。</summary>
    private static Mutex? _mutex;

    private static string _identity = string.Empty;
    private static string _pipeName = string.Empty;

    private static Action? _activationHandler;
    private static bool _activationPending;

    /// <summary>本次运行的身份（安装目录哈希前 16 位），只用于日志排查。</summary>
    public static string Identity
    {
        get { lock (Gate) return _identity; }
    }

    /// <summary>
    /// 抢占「唯一实例」的位置。
    /// </summary>
    /// <returns>
    /// <c>true</c> = 本进程是主实例，照常启动；
    /// <c>false</c> = 已经有实例在跑，并且已经请它把窗口叫到最前，本进程应当直接退出。
    /// </returns>
    public static bool TryAcquireOrActivate()
    {
        try
        {
            var identity = BuildIdentity();

            lock (Gate)
            {
                _identity = identity;
                _pipeName = "vbl-" + identity;
            }

            // Windows 上用 Local\ 前缀：同一台机器上不同用户（含 RDP 会话）各跑各的一份，
            // 不会因为「另一个用户已经开过」而自己起不来。
            var mutexName = OperatingSystem.IsWindows() ? @"Local\vbl-" + identity : "vbl-" + identity;

            Mutex mutex;
            bool createdNew;

            try
            {
                mutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
            {
                // 平台不支持命名互斥体（少见）或权限不足：放行。多开总比开不起来强。
                Serilog.Log.Warning(ex, "无法创建单实例互斥体，本次不限制多开");
                return true;
            }

            if (!createdNew && !TryTakeOver(mutex))
            {
                // 真的已经有一个在跑：请它前置，然后让调用方退出。
                mutex.Dispose();

                var activated = RequestActivation();
                Serilog.Log.Information(
                    "已有启动器实例在运行（{Identity}），本次退出；前置请求{Result}",
                    identity, activated ? "已送达" : "未送达（主实例可能仍在启动）");

                return false;
            }

            lock (Gate)
                _mutex = mutex;

            StartServer();
            return true;
        }
        catch (Exception ex)
        {
            // 兜底：守卫自己出问题绝不能拦住启动。
            Serilog.Log.Warning(ex, "单实例检查失败，按主实例继续启动");
            return true;
        }
    }

    /// <summary>
    /// 命名对象已存在，但持有它的进程可能已经退出了（句柄还没被系统回收干净）。
    /// 能立刻拿到锁就说明没人真的在跑 —— 这次由我们接管。
    /// </summary>
    private static bool TryTakeOver(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一个持有者没释放就死了：锁现在是我们的。
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "判断互斥体归属失败，按「已有实例」处理");
            return false;
        }
    }

    /// <summary>
    /// 释放互斥体。由 <c>Program.Main</c> 的 finally 调用；进程结束时系统也会释放，
    /// 这里只是让「关掉马上再开」不必等。
    /// </summary>
    public static void Release()
    {
        lock (Gate)
        {
            try { _mutex?.ReleaseMutex(); } catch { /* 未持有就释放会抛，忽略 */ }
            try { _mutex?.Dispose(); } catch { /* 关不掉也无所谓，进程马上结束 */ }
            _mutex = null;
        }
    }

    /// <summary>
    /// 登记「被第二个实例请求前置时该做什么」。
    ///
    /// 主实例可能还没建好主窗口就收到了请求，所以这里会记住一次未处理的请求，
    /// 等窗口就绪后再补发 —— 否则「启动到一半又双击一次」就等于什么都没发生。
    /// </summary>
    public static void SetActivationHandler(Action handler)
    {
        bool runNow;

        lock (Gate)
        {
            _activationHandler = handler;
            runNow = _activationPending;
            _activationPending = false;
        }

        if (runNow)
            Invoke(handler);
    }

    // ============================ 第二个实例：发请求 ============================

    /// <summary>
    /// 连上主实例的命名管道并请求前置。连不上会重试到 <see cref="ActivateTimeout"/> 为止 ——
    /// 主实例可能刚启动、管道还没建好。
    /// </summary>
    private static bool RequestActivation()
    {
        var pipeName = _pipeName;
        var deadline = DateTime.UtcNow + ActivateTimeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                client.Connect(350);

                using var writer = new StreamWriter(client, new UTF8Encoding(false), 128, leaveOpen: true)
                {
                    AutoFlush = true
                };
                writer.WriteLine(ActivateMessage);

                using var reader = new StreamReader(client, Encoding.UTF8, false, 128, leaveOpen: true);
                var reply = reader.ReadLine();

                if (string.Equals(reply?.Trim(), AckMessage, StringComparison.Ordinal))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException
                                           or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // 正常现象：主实例还在启动，管道没建好。稍等重试。
            }

            Thread.Sleep(150);
        }

        return false;
    }

    // ============================ 主实例：收请求 ============================

    private static void StartServer()
    {
        var thread = new Thread(ServerLoop)
        {
            IsBackground = true,
            Name = "VBL-SingleInstance"
        };
        thread.Start();
    }

    /// <summary>
    /// 常驻等连接。一次只服务一个请求，处理完立刻重建管道等下一个。
    /// 任何异常都只睡一下再来 —— 这条线程死掉会让「双击第二次没反应」的观感回来。
    /// </summary>
    private static void ServerLoop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);

                server.WaitForConnection();

                using var reader = new StreamReader(server, Encoding.UTF8, false, 128, leaveOpen: true);
                var line = reader.ReadLine();

                if (string.Equals(line?.Trim(), ActivateMessage, StringComparison.Ordinal))
                {
                    RaiseActivation();

                    using var writer = new StreamWriter(server, new UTF8Encoding(false), 128, leaveOpen: true)
                    {
                        AutoFlush = true
                    };
                    writer.WriteLine(AckMessage);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "单实例管道异常，稍后重建");
                Thread.Sleep(300);
            }
        }
    }

    private static void RaiseActivation()
    {
        Action? handler;

        lock (Gate)
        {
            handler = _activationHandler;

            // 主窗口还没建好：先记下，等 SetActivationHandler 时补发。
            if (handler is null)
            {
                _activationPending = true;
                return;
            }
        }

        Invoke(handler);
    }

    /// <summary>切回 UI 线程再动窗口 —— 这里是从管道线程进来的。</summary>
    private static void Invoke(Action handler)
    {
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(handler);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "派发单实例激活请求失败");
        }
    }

    /// <summary>
    /// 由安装目录算出一个稳定短名。
    ///
    /// 用目录而不是 PID / 用户名：同一份安装每次算出来的名字必须一样，
    /// 换台机器、换个用户目录也照样稳定。哈希只是为了把可能含空格 / 中文 / 超长的
    /// 路径塞进「互斥体名 + 管道名」允许的字符与长度里。
    /// </summary>
    private static string BuildIdentity()
    {
        var root = AppContext.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(root));

        var builder = new StringBuilder(16);
        for (var i = 0; i < 8; i++)
            builder.Append(hash[i].ToString("x2"));

        return builder.ToString();
    }
}
