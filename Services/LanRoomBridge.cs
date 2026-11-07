using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 让「房间」自己出现在多人游戏的服务器列表里。
    ///
    /// <para>
    /// <b>问题</b>：EasyTier 是点对点虚拟网卡，没有广播域。房主在游戏里「对局域网开放」后，
    /// 原版的局域网广播（UDP 224.0.2.60:4445）传不过去 —— 房客的多人游戏列表永远是空的，
    /// 只能去「直接连接」手抄房主的虚拟 IP 和一个随机端口。更糟的是那个端口每次开房都变。
    /// </para>
    ///
    /// <para>
    /// <b>解法</b>：房主这边把随机端口「钉」成固定端口，房客这边直接把房间写进服务器列表。
    /// </para>
    /// <list type="number">
    /// <item>房主：盯住游戏的 <c>logs/latest.log</c>，读到 <c>Local game hosted on port 54321</c>
    /// 就说明开房了；随即在本机 <see cref="RoomPort"/>（25565）起一个 TCP 转发，
    /// 目标指向 <c>127.0.0.1:54321</c>。世界关掉时日志里会出现 <c>Stopping server</c>，
    /// 转发跟着关掉。</item>
    /// <item>房客：在虚拟网络里对每个成员试连 <see cref="RoomPort"/>，谁应了谁就是房主
    /// （只有房主会监听这个端口）；拿到地址后把「房间 xxx → 房主IP:25565」写进
    /// <c>servers.dat</c>。</item>
    /// </list>
    ///
    /// <para>
    /// 这样一来端口永远是 25565，房客也不需要知道房主游戏里随机到了哪个端口；
    /// 房主关房后转发消失，房客的探测自然失败，不会写进一条连不上的死记录。
    /// </para>
    /// </summary>
    public sealed class LanRoomBridge : IDisposable
    {
        /// <summary>房主对外的固定端口。选 25565 是因为它就是玩家心里的「服务器默认端口」。</summary>
        public const int RoomPort = 25565;

        /// <summary>轮询间隔：够快能跟上开房，又不至于每秒都在读日志和试连。</summary>
        private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(1500);

        /// <summary>试连房主端口的超时。虚拟网络内延迟通常几十毫秒，800ms 足够宽容。</summary>
        private const int ProbeTimeoutMs = 800;

        private readonly EasyTierService _easyTier;
        private readonly LanLogWatcher _watcher = new();
        private readonly RoomRelay _relay = new();
        private readonly CancellationTokenSource _cts = new();

        private Task? _loop;

        private string _status = string.Empty;
        private string _guestHost = string.Empty;
        private string _lastGuestError = string.Empty;

        public LanRoomBridge(EasyTierService easyTier)
        {
            _easyTier = easyTier;
        }

        /// <summary>给界面看的一句话状态；空串表示不显示。</summary>
        public string StatusText => _status;

        /// <summary>房客侧：已经找到的房主地址（空串 = 还没找到）。</summary>
        public string GuestHost => _guestHost;

        /// <summary>状态变化（可能来自后台线程，订阅方自己切 UI 线程）。</summary>
        public event Action? Changed;

        /// <summary>启动轮询。重复调用无副作用。</summary>
        public void Start()
        {
            if (_loop is not null)
                return;

            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 这个循环在后台一直跑，漏一个异常就是整个进程挂掉
                    Serilog.Log.Debug(ex, "联机房间桥接轮询出错");
                }

                try
                {
                    await Task.Delay(TickInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task TickAsync(CancellationToken ct)
        {
            if (!_easyTier.IsRunning)
            {
                _watcher.Reset();
                _relay.Stop();
                _guestHost = string.Empty;
                _lastGuestError = string.Empty;
                SetStatus(string.Empty);
                return;
            }

            if (_easyTier.IsHost)
                HostTick();
            else
                await GuestTickAsync(ct).ConfigureAwait(false);
        }

        // ==================== 房主 ====================

        private void HostTick()
        {
            var instanceId = AppServices.GameSession?.Current?.InstanceId;

            if (string.IsNullOrWhiteSpace(instanceId))
            {
                _watcher.Reset();
                _relay.Stop();
                SetStatus("启动游戏后按 Esc → 对局域网开放，房间会自动出现在好友的服务器列表里");
                return;
            }

            var logPath = Path.Combine(
                GameInstanceHelper.ResolveInstanceFolder(instanceId, "logs"), "latest.log");

            if (!File.Exists(logPath))
            {
                _watcher.Reset();
                _relay.Stop();
                SetStatus("启动游戏后按 Esc → 对局域网开放，房间会自动出现在好友的服务器列表里");
                return;
            }

            _watcher.Track(logPath);
            _watcher.Poll();

            var port = _watcher.Port;

            if (port is null)
            {
                _relay.Stop();
                SetStatus("启动游戏后按 Esc → 对局域网开放，房间会自动出现在好友的服务器列表里");
                return;
            }

            // 只绑 EasyTier 的虚拟网卡，不绑 0.0.0.0：
            // 绑全地址等于让同一个 Wi-Fi / 公司网里的任何人都能绕过联机码直接进房主的游戏。
            // 比较用规范化后的地址，避免「10.126.126.2」与「10.126.126.2 」这种差异导致反复重建。
            var virtualIp = _easyTier.LocalVirtualIp?.Trim() ?? string.Empty;
            var wantedBind = RoomRelay.ParseBindAddress(virtualIp)?.ToString() ?? string.Empty;

            if (!_relay.IsRunning
                || _relay.TargetPort != port.Value
                || !string.Equals(_relay.PreferredAddress, wantedBind, StringComparison.OrdinalIgnoreCase))
            {
                var error = _relay.Start(port.Value, virtualIp);

                if (error is not null)
                {
                    Serilog.Log.Warning("房间端口转发启动失败：{Error}", error);
                    SetStatus($"房间转发没能启动：{error}");
                    return;
                }

                Serilog.Log.Information("房间已开：本机 {Port} → 局域网端口 {Lan}", RoomPort, port.Value);
            }

            SetStatus($"房间已就绪：好友的多人游戏列表里会直接出现「{RoomName()}」");
        }

        // ==================== 房客 ====================

        private async Task GuestTickAsync(CancellationToken ct)
        {
            var peers = _easyTier.Peers
                .Where(p => !p.IsSelf && !string.IsNullOrWhiteSpace(p.Ipv4))
                .Select(p => p.Ipv4.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (peers.Count == 0)
            {
                _guestHost = string.Empty;
                SetStatus("等待房主开房…");
                return;
            }

            // 已经找到过就继续用，直到连不通为止（房主关房后端口会消失）
            if (_guestHost.Length > 0
                && peers.Contains(_guestHost, StringComparer.OrdinalIgnoreCase)
                && await IsPortOpenAsync(_guestHost, ct).ConfigureAwait(false))
            {
                WriteEntry();
                return;
            }

            _guestHost = string.Empty;

            foreach (var peer in peers)
            {
                if (await IsPortOpenAsync(peer, ct).ConfigureAwait(false))
                {
                    _guestHost = peer;
                    break;
                }
            }

            if (_guestHost.Length == 0)
            {
                SetStatus("等待房主在游戏里「对局域网开放」…");
                return;
            }

            Serilog.Log.Information("房间发现：房主 {Host}:{Port}", _guestHost, RoomPort);
            WriteEntry();
        }

        /// <summary>把房间写进当前实例的 servers.dat，并更新界面状态。</summary>
        private void WriteEntry()
        {
            var name = RoomName();
            var gameDir = ResolveGameDir();

            var ok = ServerListService.TryEnsureEntry(
                gameDir, name, _guestHost, RoomPort, out var error);

            if (ok)
            {
                _lastGuestError = string.Empty;
                SetStatus($"已把「{name}」加入服务器列表，进游戏打开多人游戏双击就能进");
                return;
            }

            // 同样的错误只提示一次，避免每 1.5 秒刷一遍相同文案
            if (!string.Equals(_lastGuestError, error, StringComparison.Ordinal))
            {
                _lastGuestError = error;
                Serilog.Log.Warning("写入房间到服务器列表失败：{Error}", error);
            }

            SetStatus($"房间已找到（{_guestHost}），但没能写进服务器列表：{error}");
        }

        /// <summary>
        /// 写进哪个 gameDir：开着版本隔离时 servers.dat 在实例目录里，关着时在 .minecraft 根。
        /// 实例优先取「正在跑的那个」，没有就用联机页选中的那个。
        /// </summary>
        private static string ResolveGameDir()
        {
            var instanceId = AppServices.GameSession?.Current?.InstanceId;

            if (string.IsNullOrWhiteSpace(instanceId))
                instanceId = SettingsService.Current.LanModInstanceId;

            return GameInstanceHelper.ResolveInstanceFolder(instanceId, string.Empty);
        }

        // ==================== 公共小工具 ====================

        private string RoomName()
        {
            var key = _easyTier.NetworkKey?.Trim() ?? string.Empty;

            if (key.Length == 0)
                return "房间";

            // U/ABCD-EFGH-JKLM-NPQR → 取最后两组，够分辨又不至于把 20 个字符全塞进列表
            var groups = key.Split('-', StringSplitOptions.RemoveEmptyEntries);
            var tail = groups.Length >= 2 ? $"{groups[^2]}-{groups[^1]}" : key;

            return $"房间 {tail}";
        }

        private static async Task<bool> IsPortOpenAsync(string host, CancellationToken ct)
        {
            try
            {
                using var client = new TcpClient();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ProbeTimeoutMs);

                await client.ConnectAsync(host, RoomPort, timeout.Token)
                            .ConfigureAwait(false);

                return client.Connected;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;   // 超时 = 这个成员不是房主
            }
            catch
            {
                return false;
            }
        }

        private void SetStatus(string text)
        {
            if (string.Equals(_status, text, StringComparison.Ordinal))
                return;

            _status = text;

            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "联机房间状态通知失败");
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* 已释放 */ }

            _relay.Stop();
            _watcher.Reset();
            _cts.Dispose();
        }

        // ==================== 日志尾巴 ====================

        /// <summary>
        /// 盯 <c>logs/latest.log</c> 的新增内容。
        ///
        /// <para>
        /// 为什么不去监听 UDP 4445：游戏自己已经绑住了那个端口，再抢一份在 Windows 上
        /// 未必收得到组播（还要处理多网卡 join），而日志里那句话是游戏自己打的，
        /// 不管装没装联机模组都在。
        /// </para>
        /// </summary>
        private sealed class LanLogWatcher
        {
            /// <summary>开房那句：<c>[Server thread/INFO]: Local game hosted on port 54321</c>。</summary>
            private static readonly Regex HostedPattern = new(
                @"Local game hosted on port (\d+)", RegexOptions.Compiled);

            /// <summary>世界关闭时会打这句，端口随之失效。</summary>
            private static readonly Regex StoppedPattern = new(
                @"Stopping server", RegexOptions.Compiled);

            private const int TailBytes = 128 * 1024;

            private string _path = string.Empty;
            private long _position;
            private readonly StringBuilder _pending = new();

            /// <summary>当前有效的局域网端口；没开房 / 已关房时为 null。</summary>
            public int? Port { get; private set; }

            /// <summary>切换到另一个实例的日志（换实例或重开游戏时）。</summary>
            public void Track(string path)
            {
                if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
                    return;

                _path = path;
                _pending.Clear();
                Port = null;

                try
                {
                    // 从文件尾部附近开始读：可能在我们注意到之前游戏就已经开好房了
                    var length = new FileInfo(path).Length;
                    _position = Math.Max(0, length - TailBytes);
                }
                catch
                {
                    _position = 0;
                }
            }

            public void Poll()
            {
                if (_path.Length == 0)
                    return;

                try
                {
                    using var stream = new FileStream(
                        _path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    if (stream.Length < _position)
                    {
                        // 游戏重启会把 latest.log 截断重写
                        _position = 0;
                        _pending.Clear();
                        Port = null;
                    }

                    if (stream.Length == _position)
                        return;

                    stream.Seek(_position, SeekOrigin.Begin);

                    using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
                    var chunk = reader.ReadToEnd();
                    _position = stream.Position;

                    if (chunk.Length == 0)
                        return;

                    _pending.Append(chunk);

                    var text = _pending.ToString();
                    var lastBreak = text.LastIndexOf('\n');

                    if (lastBreak < 0)
                        return;   // 半行，等下一次

                    var complete = text[..lastBreak];
                    _pending.Clear();
                    _pending.Append(text[(lastBreak + 1)..]);

                    foreach (var line in complete.Split('\n'))
                        HandleLine(line);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "读取游戏日志失败：{Path}", _path);
                }
            }

            private void HandleLine(string line)
            {
                if (StoppedPattern.IsMatch(line))
                {
                    Port = null;
                    return;
                }

                var match = HostedPattern.Match(line);

                if (match.Success && int.TryParse(match.Groups[1].Value, out var port))
                    Port = port;
            }

            public void Reset()
            {
                _path = string.Empty;
                _position = 0;
                _pending.Clear();
                Port = null;
            }
        }

        // ==================== TCP 转发 ====================

        /// <summary>
        /// 把固定端口上的连接转给本机的局域网世界端口。
        ///
        /// <para>
        /// 目标永远是 <c>127.0.0.1</c>：转发跑在房主自己的机器上，游戏也在这台机器上，
        /// 绕虚拟网卡再去连自己只会多一跳、还容易被防火墙拦。
        /// </para>
        /// </summary>
        private sealed class RoomRelay : IDisposable
        {
            private TcpListener? _listener;
            private CancellationTokenSource? _cts;

            public bool IsRunning => _listener is not null;

            public int TargetPort { get; private set; }

            /// <summary>
            /// 最近一次请求绑定的网卡地址；空串表示绑在 <c>0.0.0.0</c>。
            ///
            /// <para>
            /// 用它跟「当前虚拟 IP」比对：EasyTier 重新加入网络后地址可能变，
            /// 变了就得重建监听，否则房客连的是旧地址。记的是**请求值**而不是实际绑定值 ——
            /// 虚拟网卡一时没就绪而退回全地址时，不该每 1.5 秒重建一次连接。
            /// </para>
            /// </summary>
            public string PreferredAddress { get; private set; } = string.Empty;

            /// <summary>
            /// 开始监听；成功返回 null，失败返回原因（端口被占用 / 没权限）。
            /// </summary>
            /// <param name="targetPort">本机游戏「对局域网开放」后真实的端口。</param>
            /// <param name="preferAddress">
            /// 优先绑定的本机地址（EasyTier 的虚拟 IP）。
            ///
            /// <para>
            /// <b>为什么不能一律绑 0.0.0.0</b>：那等于把这台机器在所有网卡上都挂上 25565 ——
            /// 同一个 Wi-Fi、同一个公司网里的任何人都能绕过联机码直接进房主的游戏。
            /// 绑虚拟网卡之后，只有虚拟网络里的成员连得到，和「房间」的语义一致。
            /// </para>
            ///
            /// <para>
            /// 拿不到虚拟 IP，或者这个地址在本机绑不上（网卡刚拆、地址还没配上）时，
            /// 退回 <c>0.0.0.0</c> 保证联机可用 —— 功能不能因为加固而失效。
            /// </para>
            /// </param>
            public string? Start(int targetPort, string? preferAddress)
            {
                Stop();

                var preferred = ParseBindAddress(preferAddress);
                PreferredAddress = preferred?.ToString() ?? string.Empty;

                if (preferred is not null)
                {
                    try
                    {
                        return Listen(preferred, targetPort);
                    }
                    catch (SocketException)
                    {
                        // 虚拟网卡还没就绪：退回全地址，保证这次开房能用；
                        // PreferredAddress 仍记着虚拟 IP，等它变了才会重建。
                        Serilog.Log.Debug("绑定虚拟网卡 {Address} 失败，暂时回退到 0.0.0.0", preferred);
                    }
                }

                return Listen(IPAddress.Any, targetPort);
            }

            private string? Listen(IPAddress address, int targetPort)
            {
                try
                {
                    var listener = new TcpListener(address, RoomPort);
                    listener.Start();

                    _listener = listener;
                    TargetPort = targetPort;
                    _cts = new CancellationTokenSource();

                    _ = Task.Run(() => AcceptLoopAsync(listener, targetPort, _cts.Token));

                    return null;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    return $"{RoomPort} 端口被别的程序占用了（可能本机已经开了个服务器）";
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            }

            /// <summary>
            /// 只接受「能用来绑定的本机单播地址」：回环、Any、IPv6 一律当成「没给地址」。
            /// 回环绑上去房客就连不到了，那不是我们想要的「更安全」，是「不能用」。
            /// </summary>
            internal static IPAddress? ParseBindAddress(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    return null;

                if (!IPAddress.TryParse(raw.Trim(), out var address))
                    return null;

                if (address.AddressFamily != AddressFamily.InterNetwork)
                    return null;

                if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
                    return null;

                return address;
            }

            private async Task AcceptLoopAsync(
                TcpListener listener, int targetPort, CancellationToken ct)
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient client;

                    try
                    {
                        client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Debug(ex, "接受房间连接失败");
                        continue;
                    }

                    _ = Task.Run(() => PumpAsync(client, targetPort, ct));
                }
            }

            private static async Task PumpAsync(TcpClient client, int targetPort, CancellationToken ct)
            {
                using (client)
                {
                    using var upstream = new TcpClient();

                    try
                    {
                        await upstream.ConnectAsync(
                            IPAddress.Loopback, targetPort, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 世界刚关掉 / 正在加载，局域网上还没起来 —— 直接断开即可
                        Serilog.Log.Debug(ex, "连接本机局域网世界失败：{Port}", targetPort);
                        return;
                    }

                    client.NoDelay = true;
                    upstream.NoDelay = true;

                    var down = client.GetStream();
                    var up = upstream.GetStream();

                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    var upTask = PumpAsync(up, down, linked.Token);
                    var downTask = PumpAsync(down, up, linked.Token);

                    await Task.WhenAny(upTask, downTask).ConfigureAwait(false);

                    // 一边结束就掐掉另一边的读写，否则连接会挂在那儿不释放
                    try { linked.Cancel(); } catch { /* 忽略 */ }

                    await Task.WhenAny(Task.WhenAll(upTask, downTask), Task.Delay(1000))
                              .ConfigureAwait(false);
                }
            }

            private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
            {
                try
                {
                    await from.CopyToAsync(to, ct).ConfigureAwait(false);
                }
                catch
                {
                    // 连接被对端关掉是常态，不是错误
                }
            }

            public void Stop()
            {
                try { _cts?.Cancel(); } catch { /* 忽略 */ }

                try { _listener?.Stop(); } catch { /* 忽略 */ }

                try { _cts?.Dispose(); } catch { /* 忽略 */ }

                _cts = null;
                _listener = null;
                TargetPort = 0;
            }

            public void Dispose() => Stop();
        }
    }
}
