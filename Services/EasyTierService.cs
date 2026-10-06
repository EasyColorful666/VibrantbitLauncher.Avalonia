using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 虚拟网络里的一个成员节点（由 <c>easytier-cli peer</c> 的输出解析而来）。
    /// </summary>
    public sealed class EasyTierPeer
    {
        /// <summary>节点在虚拟网络里的 IPv4 地址，例如 10.126.126.2。</summary>
        public string Ipv4 { get; init; } = string.Empty;

        /// <summary>节点主机名（对方电脑名，或 EasyTier 自动生成的短名）。</summary>
        public string Hostname { get; init; } = string.Empty;

        /// <summary>EasyTier 给出的链路代价列：Local / p2p / relay / 数字。</summary>
        public string Cost { get; init; } = string.Empty;

        /// <summary>延迟（毫秒）；未知时为 -1。</summary>
        public double Latency { get; init; } = -1;

        /// <summary>是不是本机自己那一行。</summary>
        public bool IsSelf { get; init; }

        public string NameText => string.IsNullOrWhiteSpace(Hostname)
            ? (IsSelf ? "本机" : Ipv4)
            : (IsSelf ? $"{Hostname}（我）" : Hostname);

        public string IpText => Ipv4;

        public string LatencyText => Latency >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Latency:0.##} ms")
            : "—";

        /// <summary>把 EasyTier 的代价列翻译成人话。</summary>
        public string LinkText => Cost switch
        {
            "Local" => "本机",
            "p2p" => "P2P 直连",
            "relay" => "中继转发",
            "" => "—",
            _ => double.TryParse(Cost, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                ? "中继转发"
                : Cost
        };
    }

    /// <summary>
    /// EasyTier 联机内核：以外挂进程的方式驱动 <c>easytier-core.exe</c>，把两台机器拉进同一个虚拟局域网。
    ///
    /// 房主与房客做的是同一件事 —— 用相同的「密钥」作为网络名 + 网络密码加入公共中继服务器；
    /// 区别只在于房主还要在游戏里「对局域网开放」，并把密钥和自己的虚拟 IP 告诉房客。
    ///
    /// 之所以是外挂进程而不是 NuGet 库：EasyTier 官方只提供可执行文件，
    /// 核心会在系统里创建虚拟网卡（需要管理员权限），所以这里用进程方式管理它的生命周期。
    /// </summary>
    public sealed class EasyTierService : IDisposable
    {
        /// <summary>EasyTier 社区免费共享节点，无需自建服务器即可牵线。</summary>
        public const string DefaultRelayServer = "tcp://easytier.weiai.org.cn:11010";

        /// <summary>核心可执行文件名。</summary>
        private const string CoreFileName = "easytier-core.exe";

        /// <summary>命令行客户端名，用来读取成员列表。</summary>
        private const string CliFileName = "easytier-cli.exe";

        /// <summary>虚拟网络里出现的 IPv4（EasyTier 默认分配 10.x.x.x 段）。</summary>
        private static readonly Regex VirtualIpv4Regex =
            new(@"(?<![\d.])(10\.\d{1,3}\.\d{1,3}\.\d{1,3})(?![\d])", RegexOptions.Compiled);

        private static readonly Regex Ipv4Regex =
            new(@"^(?:\d{1,3}\.){3}\d{1,3}$", RegexOptions.Compiled);

        private readonly object _gate = new();
        private readonly List<EasyTierPeer> _peers = new();
        private readonly Queue<string> _recentOutput = new();

        private Process? _core;
        private System.Timers.Timer? _pollTimer;

        // 事件处理器存成字段，退订时才能精确摘掉（lambda 每次都是新实例，退订无效）。
        private DataReceivedEventHandler? _outputHandler;
        private DataReceivedEventHandler? _errorHandler;
        private EventHandler? _exitedHandler;

        /// <summary>从核心日志里捞到的本机 IP 候选值（CLI 拿到权威值后以 CLI 为准）。</summary>
        private string _ipFromLog = string.Empty;

        /// <summary>停止时抑制 Exited 事件里的「异常退出」处理。</summary>
        private bool _stopping;

        /// <summary>状态有变化时触发（可能在后台线程，订阅方自行切回 UI 线程）。</summary>
        public event Action? StateChanged;

        // ============================ 对外状态 ============================

        /// <summary>EasyTier 核心是否正在运行。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>本机是房主还是房客（仅用于界面展示）。</summary>
        public bool IsHost { get; private set; }

        /// <summary>当前使用的联机密钥（同时作为网络名与网络密码）。</summary>
        public string NetworkKey { get; private set; } = string.Empty;

        /// <summary>当前使用的中继服务器。</summary>
        public string RelayServer { get; private set; } = DefaultRelayServer;

        /// <summary>本机在虚拟网络里的地址。</summary>
        public string LocalVirtualIp { get; private set; } = string.Empty;

        /// <summary>
        /// 本次使用的本地监听端口。EasyTier 默认监听 tcp/udp 11010、wg 11011，
        /// 但 Windows 会把成段的端口保留给 Hyper-V / WSL 的 NAT
        /// （用 <c>netsh interface ipv4 show excludedportrange protocol=tcp</c> 能查到，
        /// 常见机器上 10977-11076 整段都在里面）。落在保留段里的端口绑定会直接失败并报
        /// WSAEACCES(10013)，核心随即退出。所以这里显式挑一个本机真正能绑上的端口。
        /// </summary>
        public int ListenPort { get; private set; }

        /// <summary>一句话状态，直接显示给用户。</summary>
        public string StatusMessage { get; private set; } = "未启动";

        /// <summary>最近一次错误信息，供界面提示。</summary>
        public string? LastError { get; private set; }

        /// <summary>成员列表快照（含本机自己那一行）。</summary>
        public IReadOnlyList<EasyTierPeer> Peers
        {
            get
            {
                lock (_gate)
                    return _peers.ToArray();
            }
        }

        // ============================ 环境探测 ============================

        /// <summary>程序所在目录（VBL 数据目录的基准）。</summary>
        public static string AppDirectory => AppContext.BaseDirectory;

        /// <summary>EasyTier 安装目录：<c>&lt;程序目录&gt;\VBL</c>。</summary>
        public static string VblDirectory => Path.Combine(AppDirectory, "VBL");

        /// <summary>是否已安装 EasyTier 核心（找不到返回 null）。</summary>
        public static string? ResolveCoreExecutable() => ResolveTool(CoreFileName);

        /// <summary>命令行客户端的完整路径（找不到返回 null）。</summary>
        public static string? ResolveCliExecutable() => ResolveTool(CliFileName);

        /// <summary>EasyTier 核心是否可用。</summary>
        public static bool IsCoreInstalled => ResolveCoreExecutable() != null;

        /// <summary>
        /// 当前进程是否以管理员身份运行。EasyTier 需要创建虚拟网卡，
        /// 没有管理员权限时核心会在启动后立刻退出。
        /// </summary>
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// 先在约定目录 <c>VBL\easytier-windows-x86_64\</c> 找，找不到则在整个 VBL 目录里递归找，
        /// 这样将来 EasyTier 改了压缩包内的目录名也不会失联。
        /// </summary>
        private static string? ResolveTool(string fileName)
        {
            foreach (var root in new[] { VblDirectory, Path.Combine(Environment.CurrentDirectory, "VBL") })
            {
                var direct = Path.Combine(root, "easytier-windows-x86_64", fileName);
                if (File.Exists(direct))
                    return direct;

                if (!Directory.Exists(root))
                    continue;

                try
                {
                    var hit = Directory
                        .EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                        .FirstOrDefault();

                    if (hit != null)
                        return hit;
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "EasyTier 目录扫描失败");
                }
            }

            return null;
        }

        /// <summary>生成一串 12 位密钥，字母数字混排、去掉容易看混的字符。</summary>
        public static string GenerateNetworkKey()
        {
            const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
            var buffer = new byte[12];

            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(buffer);

            var sb = new StringBuilder(12);
            foreach (var b in buffer)
                sb.Append(alphabet[b % alphabet.Length]);

            return sb.ToString();
        }

        // ============================ 端口探测 ============================

        /// <summary>
        /// 挑一个本机能真正绑上的端口给核心做监听。
        ///
        /// 做法是让系统从临时端口里给一个（Windows 的临时端口分配本身就会跳过被保留的区间），
        /// 再验证同一个端口的 UDP 也能绑上 —— EasyTier 要同时监听 tcp 与 udp。
        /// </summary>
        public static int PickListenerPort()
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var candidate = ResolveEphemeralPort();
                if (candidate <= 0)
                    break;

                if (CanBindUdp(candidate))
                    return candidate;
            }

            Serilog.Log.Warning("没有找到可用的监听端口，EasyTier 将回退到它自己的默认端口");
            return 0;
        }

        private static int ResolveEphemeralPort()
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, 0);
                listener.Start();

                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();

                return port;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "获取临时端口失败");
                return 0;
            }
        }

        private static bool CanBindUdp(int port)
        {
            try
            {
                using var client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "UDP 端口 {Port} 不可用", port);
                return false;
            }
        }

        // ============================ 启停 ============================

        /// <summary>
        /// 启动 EasyTier 核心并加入虚拟网络。返回 null 表示成功，否则是给用户看的错误文本。
        /// </summary>
        public async Task<string?> StartAsync(string key, bool isHost, string relayServer)
        {
            Stop();

            var core = ResolveCoreExecutable();
            if (core == null)
                return "未找到 EasyTier 核心程序，请先点击「下载 EasyTier 核心」。";

            key = (key ?? string.Empty).Trim();
            if (key.Length < 4)
                return "密钥太短了，请至少填 4 位（建议 8 位以上，避免和别人的网络撞名）。";

            var endpoint = NormalizeRelayServer(relayServer);
            var listenPort = PickListenerPort();

            var startInfo = new ProcessStartInfo
            {
                FileName = core,
                WorkingDirectory = Path.GetDirectoryName(core) ?? VblDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // 参数与官方文档一致：网络名与网络密码都取密钥，-p 指定公共中继节点。
            startInfo.ArgumentList.Add("--network-name");
            startInfo.ArgumentList.Add(key);
            startInfo.ArgumentList.Add("--network-secret");
            startInfo.ArgumentList.Add(key);

            if (!string.IsNullOrEmpty(endpoint))
            {
                startInfo.ArgumentList.Add("-p");
                startInfo.ArgumentList.Add(endpoint);
            }

            // 必须显式要一个虚拟 IP：按官方说明，不传 -i 时「此节点将仅转发数据包，不会创建 TUN 设备」，
            // 也就是连上了也拿不到地址、游戏里用不了。--dhcp 让核心自己分配并建网卡，
            // 多台房客同时加入也不会撞 IP。
            startInfo.ArgumentList.Add("--dhcp");
            startInfo.ArgumentList.Add("true");

            // 显式指定监听端口，绕开 Windows 的保留端口段（EasyTier 默认的 11010/11011 正好落在里面）。
            // 用 url 形式而不是裸端口号，是因为裸端口号会连带开 ws/wss/wg 一堆套接字，失败面更大。
            if (listenPort > 0)
            {
                startInfo.ArgumentList.Add("-l");
                startInfo.ArgumentList.Add($"tcp://0.0.0.0:{listenPort}");
                startInfo.ArgumentList.Add("-l");
                startInfo.ArgumentList.Add($"udp://0.0.0.0:{listenPort}");
            }

            // 把物理网卡上的 UDP 广播转发给同伴，这样房客在游戏的「多人游戏」列表里能直接看到房主
            // 开的世界（Minecraft 靠 224.0.2.60:4445 广播发现局域网世界）。
            // 官方说明：仅 Windows，需要管理员权限 —— 而 EasyTier 建虚拟网卡本来就要管理员权限。
            startInfo.ArgumentList.Add("--enable-udp-broadcast-relay");
            startInfo.ArgumentList.Add("true");

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                var message = $"启动 EasyTier 失败：{ex.Message}";
                Serilog.Log.Error(ex, "启动 EasyTier 核心失败");
                return message;
            }

            if (process == null)
                return "启动 EasyTier 失败：无法创建核心进程。";

            lock (_gate)
            {
                _core = process;
                _peers.Clear();
                _recentOutput.Clear();
            }

            _ipFromLog = string.Empty;
            _stopping = false;

            // 先把「运行中」这套状态设好，再去订阅 Exited。
            // 顺序反过来的话，核心秒退时 Exited 会先把状态清成「未启动」，随后又被这里覆盖回「运行中」。
            IsRunning = true;
            IsHost = isHost;
            NetworkKey = key;
            RelayServer = endpoint;
            ListenPort = listenPort;
            LocalVirtualIp = string.Empty;
            LastError = null;
            StatusMessage = "正在加入虚拟网络…";

            Serilog.Log.Information("EasyTier 核心已拉起：监听端口 {Port}，中继 {Relay}，身份 {Role}",
                listenPort, endpoint, isHost ? "房主" : "房客");

            // 进程在 Start 与这里之间就退出了的话，订阅时会立刻收到 Exited，由 OnCoreExited 收尾。
            process.OutputDataReceived += _outputHandler = (_, e) => OnCoreOutput(e.Data);
            process.ErrorDataReceived += _errorHandler = (_, e) => OnCoreOutput(e.Data);
            process.EnableRaisingEvents = true;
            process.Exited += _exitedHandler = OnCoreExited;

            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "挂接 EasyTier 输出失败");
            }

            StartPolling();
            NotifyStateChanged();

            // 留一点时间给核心：参数不合法或缺少管理员权限时它会立刻退出。
            await Task.Delay(1500);

            // 核心在这 1.5 秒内退出的话，Exited 事件已经跑过 Stop() 并把 process 收走了
            //（对象被 Dispose 后再访问 HasExited 会抛 InvalidOperationException）。
            // 所以先确认这个进程还归我们管，再决定要不要看它的退出状态。
            if (!OwnsCore(process))
                return LastError ?? "EasyTier 核心进程已退出。";

            if (TryGetHasExited(process, out var exited) && exited)
            {
                var exitCode = TryGetExitCode(process);
                var detail = TakeRecentOutput();
                Stop();
                return DescribeStartupFailure(exitCode, detail);
            }

            return null;
        }

        /// <summary>这个进程实例是否仍是当前会话的核心（被 Stop() 收走后为 false）。</summary>
        private bool OwnsCore(Process process)
        {
            lock (_gate)
                return ReferenceEquals(_core, process);
        }

        /// <summary>安全读取 HasExited：对象已被回收时返回 false，而不是抛异常。</summary>
        private static bool TryGetHasExited(Process process, out bool hasExited)
        {
            try
            {
                hasExited = process.HasExited;
                return true;
            }
            catch (InvalidOperationException)
            {
                hasExited = false;
                return false;
            }
        }

        /// <summary>安全读取退出码：拿不到时返回 -1（界面会省略「退出码」那段）。</summary>
        private static int TryGetExitCode(Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }

        /// <summary>停止核心进程并清理状态。</summary>
        public void Stop()
        {
            _stopping = true;

            StopPolling();

            Process? process;
            lock (_gate)
            {
                process = _core;
                _core = null;
                _peers.Clear();
                _recentOutput.Clear();
            }

            if (process != null)
            {
                try
                {
                    if (_outputHandler != null)
                        process.OutputDataReceived -= _outputHandler;

                    if (_errorHandler != null)
                        process.ErrorDataReceived -= _errorHandler;

                    if (_exitedHandler != null)
                        process.Exited -= _exitedHandler;

                    if (!TryGetHasExited(process, out var exited) || !exited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(4000);
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "结束 EasyTier 进程时出错");
                }
                finally
                {
                    process.Dispose();
                    _outputHandler = null;
                    _errorHandler = null;
                    _exitedHandler = null;
                }
            }

            // 兜底：核心可能被外部手动拉起，或上一次没杀干净。
            KillStrayCores();

            IsRunning = false;
            LocalVirtualIp = string.Empty;
            _ipFromLog = string.Empty;
            StatusMessage = "未启动";

            NotifyStateChanged();
        }

        public void Dispose() => Stop();

        /// <summary>把用户填的中继地址补成 EasyTier 认的 URL 形式。</summary>
        public static string NormalizeRelayServer(string? relayServer)
        {
            var value = (relayServer ?? string.Empty).Trim();
            if (value.Length == 0)
                return DefaultRelayServer;

            return value.Contains("://", StringComparison.Ordinal)
                ? value
                : $"tcp://{value}";
        }

        // ============================ 输出解析 ============================

        private void OnCoreOutput(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            lock (_gate)
            {
                _recentOutput.Enqueue(line);

                while (_recentOutput.Count > 60)
                    _recentOutput.Dequeue();
            }

            if (_ipFromLog.Length > 0)
                return;

            // 只在提到 ipv4 / 地址分配的日志里找，避免把中继服务器或邻居的 IP 误认成本机地址。
            var looksLikeAddressLog = line.Contains("ipv4", StringComparison.OrdinalIgnoreCase)
                                      || line.Contains("address", StringComparison.OrdinalIgnoreCase)
                                      || line.Contains("assign", StringComparison.OrdinalIgnoreCase);

            if (!looksLikeAddressLog)
                return;

            var match = VirtualIpv4Regex.Match(line);
            if (!match.Success)
                return;

            _ipFromLog = match.Groups[1].Value;
            LocalVirtualIp = _ipFromLog;
            StatusMessage = "已加入虚拟网络";
            NotifyStateChanged();
        }

        private void OnCoreExited(object? sender, EventArgs e)
        {
            // 主动 Stop() 引发的退出不当作故障处理。
            if (_stopping)
                return;

            // 用事件 sender 拿退出码：Stop() 之后 _core 就没了，那时再问进程要已经晚了。
            var exitCode = sender is Process exitedProcess ? TryGetExitCode(exitedProcess) : -1;
            var detail = TakeRecentOutput();
            Stop();

            LastError = DescribeStartupFailure(exitCode, detail);
            StatusMessage = "核心进程已退出";
            NotifyStateChanged();
        }

        private string TakeRecentOutput()
        {
            lock (_gate)
                return string.Join(" / ", _recentOutput.TakeLast(6)).Trim();
        }

        private static string DescribeStartupFailure(int exitCode, string detail)
        {
            var sb = new StringBuilder("EasyTier 核心启动后立即退出了。");

            if (exitCode >= 0)
                sb.Append($"（退出码 {exitCode}）");

            if (!string.IsNullOrWhiteSpace(detail))
            {
                if (detail.Length > 300)
                    detail = detail[^300..];

                sb.Append($" 核心输出：{detail}");
            }

            if (!IsElevated)
                sb.Append(" 提示：EasyTier 需要管理员权限创建虚拟网卡，请关闭启动器后以管理员身份重新打开。");

            return sb.ToString();
        }

        /// <summary>清掉残留的 easytier-core 进程，避免旧实例占着网络名。</summary>
        private static void KillStrayCores()
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(CoreFileName);
                foreach (var stray in Process.GetProcessesByName(name))
                {
                    try
                    {
                        stray.Kill(entireProcessTree: true);
                        stray.WaitForExit(2000);
                    }
                    catch
                    {
                        // 单个进程杀不掉不影响整体流程
                    }
                    finally
                    {
                        stray.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "清理残留 EasyTier 进程失败");
            }
        }

        // ============================ 成员轮询 ============================

        private void StartPolling()
        {
            StopPolling();

            _pollTimer = new System.Timers.Timer(3000) { AutoReset = true };
            _pollTimer.Elapsed += OnPollTimerElapsed;
            _pollTimer.Start();

            // 立刻先拉一次，别让用户干等三秒。
            _ = PollPeersAsync();
        }

        private void StopPolling()
        {
            if (_pollTimer == null)
                return;

            _pollTimer.Stop();
            _pollTimer.Elapsed -= OnPollTimerElapsed;
            _pollTimer.Dispose();
            _pollTimer = null;
        }

        private void OnPollTimerElapsed(object? sender, ElapsedEventArgs e) => _ = PollPeersAsync();

        private async Task PollPeersAsync()
        {
            if (!IsRunning)
            {
                // 核心可能已经退出（例如启动瞬间就失败），这时轮询没有意义，顺手把定时器收掉，
                // 避免它在后台一直空转。
                StopPolling();
                return;
            }

            var cli = ResolveCliExecutable();
            if (cli == null)
                return;

            string output;
            try
            {
                // 优先要 JSON：表格输出的列顺序和列名会随版本变，而且单元格里带竖线分隔符，
                // 结构化输出解析起来稳得多。老版本核心不认 -o 时会打印报错，下面会退回表格解析。
                output = await RunProcessAsync(cli, new[] { "-o", "json", "peer", "list" }, 6000);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "读取 EasyTier 成员列表失败");
                return;
            }

            if (string.IsNullOrWhiteSpace(output))
                return;

            var parsed = ParsePeers(output, out var selfIp);

            // CLI 拿到的本机地址是权威值，优先于日志里捞到的候选值。
            if (!string.IsNullOrEmpty(selfIp))
                _ipFromLog = selfIp;

            lock (_gate)
            {
                _peers.Clear();
                _peers.AddRange(parsed);
            }

            LocalVirtualIp = FirstNonEmpty(selfIp, _ipFromLog);

            if (IsRunning && parsed.Count > 0)
                StatusMessage = IsHost ? "虚拟网络已就绪（房主）" : "虚拟网络已就绪（房客）";

            NotifyStateChanged();
        }

        private static string FirstNonEmpty(string a, string b) => !string.IsNullOrEmpty(a) ? a : b;

        /// <summary>
        /// 解析 <c>easytier-cli peer</c> 的输出：优先 JSON，失败再退回表格。
        /// </summary>
        private static List<EasyTierPeer> ParsePeers(string output, out string selfIp)
        {
            if (TryParsePeersJson(output, out var fromJson, out selfIp))
                return Sort(fromJson);

            return ParsePeerTable(output, out selfIp);
        }

        private static List<EasyTierPeer> Sort(List<EasyTierPeer> peers) => peers
            .OrderByDescending(p => p.IsSelf)
            .ThenBy(p => p.Ipv4, StringComparer.Ordinal)
            .ToList();

        /// <summary>
        /// 解析 <c>easytier-cli -o json peer list</c> 的输出。
        /// 字段名取自 2.6.x 的真实输出：ipv4 / hostname / cost / lat_ms / cidr 等，值都是字符串。
        /// </summary>
        private static bool TryParsePeersJson(string output, out List<EasyTierPeer> peers, out string selfIp)
        {
            peers = new List<EasyTierPeer>();
            selfIp = string.Empty;

            // CLI 可能在 JSON 前后夹带别的输出，只取第一个数组。
            var start = output.IndexOf('[');
            var end = output.LastIndexOf(']');
            if (start < 0 || end <= start)
                return false;

            try
            {
                using var document = JsonDocument.Parse(output[start..(end + 1)]);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return false;

                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    var hostname = ReadJsonText(item, "hostname");

                    // 公共中继节点不属于我们的网络，不展示。
                    if (hostname.StartsWith("PublicServer", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ipv4 = NormalizeIpv4(ReadJsonText(item, "ipv4"));
                    var cost = ReadJsonText(item, "cost");
                    var isSelf = string.Equals(cost, "Local", StringComparison.OrdinalIgnoreCase);

                    if (isSelf)
                    {
                        // 本机那一行的 ipv4 有时是空的，地址落在 cidr 里（形如 10.0.0.1/24）。
                        if (ipv4.Length == 0)
                            ipv4 = NormalizeIpv4(ReadJsonText(item, "cidr"));

                        if (ipv4.Length > 0)
                            selfIp = ipv4;
                    }

                    peers.Add(new EasyTierPeer
                    {
                        Ipv4 = ipv4,
                        Hostname = hostname,
                        Cost = cost,
                        Latency = ParseLatency(ReadJsonText(item, "lat_ms")),
                        IsSelf = isSelf
                    });
                }

                return true;
            }
            catch (JsonException ex)
            {
                Serilog.Log.Debug(ex, "解析 EasyTier JSON 输出失败，将退回表格解析");
                return false;
            }
        }

        /// <summary>
        /// 解析 <c>easytier-cli peer</c> 的表格输出（JSON 不可用时的兜底）。
        ///
        /// 真实格式是带竖线的 markdown 风格表格：
        /// <code>
        /// | ipv4 | hostname | cost  | lat(ms) | ...
        /// |------|----------|-------|---------|
        /// | 10.126.126.1 | HOST-PC | Local | - | ...
        /// </code>
        /// 所以不能按空白切词（首列会变成 "|"），必须按竖线切单元，并靠表头定位列。
        /// </summary>
        private static List<EasyTierPeer> ParsePeerTable(string output, out string selfIp)
        {
            selfIp = string.Empty;
            var result = new List<EasyTierPeer>();
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in output.Split('\n'))
            {
                if (!raw.Contains('|'))
                    continue;

                var cells = SplitTableRow(raw);
                if (cells.Count == 0)
                    continue;

                // 第一行是表头，用它确定各列的下标。
                if (columns.Count == 0)
                {
                    for (var i = 0; i < cells.Count; i++)
                        columns.TryAdd(cells[i], i);

                    continue;
                }

                // 跳过分隔行（全是短横线）。
                if (cells.All(c => c.Length == 0 || c.All(ch => ch == '-')))
                    continue;

                var hostname = ReadCell(cells, columns, "hostname");
                if (hostname.Length == 0)
                    continue;

                if (hostname.StartsWith("PublicServer", StringComparison.OrdinalIgnoreCase))
                    continue;

                var cost = ReadCell(cells, columns, "cost");
                var ipv4 = NormalizeIpv4(ReadCell(cells, columns, "ipv4", "cidr"));
                var isSelf = string.Equals(cost, "Local", StringComparison.OrdinalIgnoreCase);

                if (isSelf && ipv4.Length > 0)
                    selfIp = ipv4;

                result.Add(new EasyTierPeer
                {
                    Ipv4 = ipv4,
                    Hostname = hostname,
                    Cost = cost,
                    Latency = ParseLatency(ReadCell(cells, columns, "lat(ms)", "lat_ms", "lat")),
                    IsSelf = isSelf
                });
            }

            return Sort(result);
        }

        private static List<string> SplitTableRow(string line)
        {
            var cells = line.Split('|').Select(c => c.Trim()).ToList();

            // 行首行尾的竖线会切出空单元，去掉。
            if (cells.Count > 0 && cells[0].Length == 0)
                cells.RemoveAt(0);

            if (cells.Count > 0 && cells[^1].Length == 0)
                cells.RemoveAt(cells.Count - 1);

            return cells;
        }

        private static string ReadCell(IReadOnlyList<string> cells, Dictionary<string, int> columns, params string[] names)
        {
            foreach (var name in names)
            {
                if (columns.TryGetValue(name, out var index) && index >= 0 && index < cells.Count)
                    return cells[index];
            }

            return string.Empty;
        }

        private static string ReadJsonText(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
                return string.Empty;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty
            };
        }

        /// <summary>去掉 <c>/24</c> 这类掩码并校验，格式不对就返回空串。</summary>
        private static string NormalizeIpv4(string value)
        {
            var text = (value ?? string.Empty).Trim();

            var slash = text.IndexOf('/');
            if (slash >= 0)
                text = text[..slash];

            return Ipv4Regex.IsMatch(text) ? text : string.Empty;
        }

        /// <summary>延迟列可能是 <c>-</c> 或 <c>0.0%</c> 之类的非数字，取不到就返回 -1。</summary>
        private static double ParseLatency(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : -1d;

        private static async Task<string> RunProcessAsync(string fileName, string[] arguments, int timeoutMs)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? ".",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process == null)
                return string.Empty;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = new System.Threading.CancellationTokenSource(timeoutMs);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 忽略
                }

                return string.Empty;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }

        private void NotifyStateChanged() => StateChanged?.Invoke();
    }
}
