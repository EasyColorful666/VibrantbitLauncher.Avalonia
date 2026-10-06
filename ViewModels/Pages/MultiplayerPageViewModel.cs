using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Input;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.Views.Windows;
using VibrantbitLauncher.Services;

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace VibrantbitLauncher.ViewModels.Pages
{
    /// <summary>
    /// 多人联机页：把 <see cref="EasyTierService"/> 的状态映射到界面，并驱动房主 / 房客两条流程。
    ///
    /// 两种身份在 EasyTier 层面做的是同一件事（用同一串密钥加入同一个虚拟网络），
    /// 差别只在提示文案和「谁去游戏里开房间」：
    ///   房主：生成密钥 → 启动服务 → 游戏里「对局域网开放」→ 把密钥 + 自己的虚拟 IP 发给好友
    ///   房客：填房主给的密钥 → 启动服务 → 在「多人游戏 → 直接连接」里填房主的虚拟 IP
    /// </summary>
    public partial class MultiplayerPageViewModel : ObservableObject
    {
        private readonly EasyTierService _easyTierService;
        private readonly INotificationService _notification = AppServices.Notifications!;

        private bool _isInitialized;

        /// <summary>命令执行期间置位，用于禁用按钮并让状态文案显示「处理中…」。</summary>
        private bool _isBusyState;

        private bool IsBusy
        {
            get => _isBusyState;
            set
            {
                if (_isBusyState == value)
                    return;

                _isBusyState = value;
                OnPropertyChanged(nameof(IsBusy));
                RaiseDerivedState();
            }
        }

        /// <summary>成员列表指纹，只有真的变了才重建集合，避免每三秒刷一次导致列表闪烁。</summary>
        private string _peerFingerprint = string.Empty;

        // ===== 输入 =====
        private bool _isHostMode = true;
        private string _hostKey = string.Empty;
        private string _guestKey = string.Empty;
        private string _relayServer = EasyTierService.DefaultRelayServer;

        // ===== 运行状态（由服务推送） =====
        private bool _isRunning;
        private string _localVirtualIp = string.Empty;
        private string _statusMessage = "未启动";
        private bool _isCoreInstalled;

        public MultiplayerPageViewModel(EasyTierService easyTierService)
        {
            _easyTierService = easyTierService;
            _easyTierService.StateChanged += OnServiceStateChanged;

            // 成员集合增删后，依赖它的派生属性（空状态、房主是否已出现）要跟着刷新。
            Peers.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasNoPeers));
                OnPropertyChanged(nameof(HasRemotePeer));
                OnPropertyChanged(nameof(RemotePeerIp));
            };

            InitializeCommand = new RelayCommand(Initialize);
            StartServiceCommand = new RelayCommand(async () => await StartServiceAsync());
            StopServiceCommand = new RelayCommand(StopService);
            RegenerateKeyCommand = new RelayCommand(RegenerateKey);
            CopyTextCommand = new RelayCommand<string>(CopyText);
            PasteGuestKeyCommand = new RelayCommand(PasteGuestKey);
            DownloadCoreCommand = new RelayCommand(ShowInstallWindow);
            RestartElevatedCommand = new RelayCommand(RestartElevated);
        }

        public RelayCommand InitializeCommand { get; }
        public RelayCommand StartServiceCommand { get; }
        public RelayCommand StopServiceCommand { get; }
        public RelayCommand RegenerateKeyCommand { get; }

        /// <summary>把任意文本塞进剪贴板（密钥、虚拟 IP 都走它）。</summary>
        public RelayCommand<string> CopyTextCommand { get; }

        public RelayCommand PasteGuestKeyCommand { get; }
        public RelayCommand DownloadCoreCommand { get; }

        /// <summary>以管理员身份重新拉起启动器（EasyTier 建虚拟网卡必须要权限）。</summary>
        public RelayCommand RestartElevatedCommand { get; }

        /// <summary>虚拟网络成员（含本机自己那一行）。</summary>
        public ObservableCollection<EasyTierPeer> Peers { get; } = new();

        // ============================ 绑定属性 ============================

        /// <summary>当前是不是「房主」模式（两个单选框的互斥绑定源）。</summary>
        public bool IsHostMode
        {
            get => _isHostMode;
            set => SetRole(value);
        }

        /// <summary>与 <see cref="IsHostMode"/> 互为反值，供「我要当房客」单选框双向绑定。</summary>
        public bool IsGuestMode
        {
            get => !_isHostMode;
            set => SetRole(!value);
        }

        /// <summary>
        /// 统一切换身份。
        ///
        /// 不能直接用 <c>SetProperty</c> 分头写两个属性：两个 RadioButton 同组时，
        /// WPF 点击「房主」会先取消「房客」的勾选、再勾上「房主」，于是两个 setter 被
        /// 先后调用 —— 第一个（把房主置 true）改完后，第二个写进的又是同一个值，
        /// <c>SetProperty</c> 判定「没变」直接返回，导致依赖 <see cref="IsGuestMode"/> /
        /// <see cref="IsHostMode"/> 的两个内容面板收不到通知，界面停在切换前的状态
        /// （表现为「选回房主后，房客的输入框/提示还在」）。
        ///
        /// 这里把状态收在一处改，并**无条件**通知两个绑定属性，保证两边必然刷新。
        /// </summary>
        private void SetRole(bool isHost)
        {
            if (_isHostMode == isHost)
            {
                // 状态没变也要补发通知：RadioButton 同组切换时可能先写同值、后写新值，
                // 漏掉通知会让界面与 ViewModel 脱节。
                OnPropertyChanged(nameof(IsHostMode));
                OnPropertyChanged(nameof(IsGuestMode));
                return;
            }

            _isHostMode = isHost;
            OnPropertyChanged(nameof(IsHostMode));
            OnPropertyChanged(nameof(IsGuestMode));
            RaiseDerivedState();
        }

        /// <summary>房主侧的密钥：进页面自动生成，也可以手动改或重新生成。</summary>
        public string HostKey
        {
            get => _hostKey;
            set => SetProperty(ref _hostKey, value);
        }

        /// <summary>房客侧填的密钥（房主提供）。</summary>
        public string GuestKey
        {
            get => _guestKey;
            set => SetProperty(ref _guestKey, value);
        }

        /// <summary>EasyTier 中继服务器地址，默认用社区免费共享节点。</summary>
        public string RelayServer
        {
            get => _relayServer;
            set => SetProperty(ref _relayServer, value);
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value))
                    RaiseDerivedState();
            }
        }

        /// <summary>本机在虚拟网络里的地址，进网成功后才有值。</summary>
        public string LocalVirtualIp
        {
            get => _localVirtualIp;
            private set
            {
                if (SetProperty(ref _localVirtualIp, value))
                    RaiseDerivedState();
            }
        }

        /// <summary>服务给的一句话状态。</summary>
        public string StatusMessage
        {
            get => _statusMessage;
            private set
            {
                if (SetProperty(ref _statusMessage, value))
                    RaiseDerivedState();
            }
        }

        /// <summary>EasyTier 核心是否已下载。</summary>
        public bool IsCoreInstalled
        {
            get => _isCoreInstalled;
            private set
            {
                if (SetProperty(ref _isCoreInstalled, value))
                {
                    OnPropertyChanged(nameof(IsCoreMissing));
                    RaiseDerivedState();
                }
            }
        }

        public bool IsCoreMissing => !_isCoreInstalled;

        /// <summary>
        /// EasyTier 要创建虚拟网卡，没提权时核心通常起不来。
        /// 这里只做提示，仍然允许用户尝试（部分环境已预装驱动，反而能跑）。
        /// </summary>
        public bool NeedsElevation => !EasyTierService.IsElevated;

        // ============================ 派生状态 ============================

        public bool IsNotRunning => !_isRunning;

        /// <summary>本机虚拟 IP 拿到之前不展示地址行。</summary>
        public bool HasLocalIp => !string.IsNullOrEmpty(_localVirtualIp);

        /// <summary>成员列表空状态。</summary>
        public bool HasNoPeers => Peers.Count == 0;

        /// <summary>除了自己之外还有没有别的成员（房客侧"房主已就绪"提示用）。</summary>
        public bool HasRemotePeer => Peers.Any(p => !p.IsSelf);

        /// <summary>房客侧可直接复制给 Minecraft「直接连接」用的地址（取第一个非本机成员）。</summary>
        public string RemotePeerIp =>
            Peers.FirstOrDefault(p => !p.IsSelf)?.Ipv4 ?? string.Empty;

        public bool CanStartService => !IsBusy && !_isRunning && _isCoreInstalled;

        public bool CanStopService => !IsBusy && _isRunning;

        /// <summary>运行中和忙碌中都不给改密钥，免得核心实际用的和界面对不上。</summary>
        public bool CanEditKey => !IsBusy && !_isRunning;

        public bool CanRegenerateKey => CanEditKey && _isHostMode;

        /// <summary>状态圆点用：绿 = 可用，黄 = 进行中，红 = 未就绪。</summary>
        public string StatusLevel
        {
            get
            {
                if (IsBusy || (_isRunning && !HasLocalIp)) return "Busy";
                if (_isRunning) return "Ready";
                return "Idle";
            }
        }

        public string StatusText
        {
            get
            {
                if (IsBusy) return "处理中…";
                if (!_isCoreInstalled) return "未找到 EasyTier 核心，请先下载";
                if (!_isRunning) return "联机服务未启动";
                if (!HasLocalIp) return "正在加入虚拟网络…";

                return _isHostMode
                    ? "虚拟网络已就绪（房主），等待好友加入"
                    : "已加入虚拟网络（房客），可以在游戏里直接连接";
            }
        }

        /// <summary>页脚的当前身份文案。</summary>
        public string RoleBadge => _isHostMode ? "房主" : "房客";

        public string CurrentKey => _isHostMode ? _hostKey : _guestKey;

        // ============================ 初始化 ============================

        private void Initialize()
        {

            RefreshCoreState();

            if (_isInitialized)
            {
                PullStateFromService();
                return;
            }

            _isInitialized = true;

            var saved = SettingsService.Current;
            _relayServer = string.IsNullOrWhiteSpace(saved.EasyTierRelayServer)
                ? EasyTierService.DefaultRelayServer
                : saved.EasyTierRelayServer;
            OnPropertyChanged(nameof(RelayServer));

            _hostKey = string.IsNullOrWhiteSpace(saved.EasyTierNetworkKey)
                ? EasyTierService.GenerateNetworkKey()
                : saved.EasyTierNetworkKey;
            OnPropertyChanged(nameof(HostKey));

            PullStateFromService();
        }

        private void RefreshCoreState()
        {
            var installed = EasyTierService.IsCoreInstalled;
            if (installed == _isCoreInstalled)
                return;

            _isCoreInstalled = installed;
            OnPropertyChanged(nameof(IsCoreInstalled));
            OnPropertyChanged(nameof(IsCoreMissing));
        }

        private void RegenerateKey()
        {
            HostKey = EasyTierService.GenerateNetworkKey();
            PersistKey();
            _notification.Show($"已生成新密钥：{HostKey}", "提示");
        }

        private void ShowInstallWindow()
        {
            try
            {
                _ = AppLifetime.ShowDialogAsync(new InstallEasyTierWindow());
            }
            catch (Exception ex)
            {
                _notification.Error($"打开下载窗口失败：{ex.Message}", "错误");
                return;
            }

            RefreshCoreState();

            if (_isCoreInstalled)
            {
                _notification.Success("EasyTier 核心已就绪，现在可以启动服务了", "成功");
            }
        }

        /// <summary>
        /// 以管理员身份重新拉起启动器。EasyTier 要用管理员权限建虚拟网卡，
        /// 与其让用户自己去右键找「以管理员身份运行」，不如直接给个按钮。
        /// </summary>
        private void RestartElevated()
        {
            if (EasyTierService.IsElevated)
            {
                _notification.Show("当前已经是管理员身份", "提示");
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                _notification.Error("定位不到启动器程序，请手动右键启动器选择「以管理员身份运行」", "错误");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory
                });
            }
            catch (Exception ex)
            {
                // 用户在 UAC 弹窗点了「否」时也会走到这里（Win32 错误 1223）。
                Serilog.Log.Information(ex, "提权重启被取消或失败");
                _notification.Warning("没有获得管理员权限，已取消。也可以手动右键启动器选择「以管理员身份运行」", "提示");
                return;
            }

            // 新实例已经起来，把当前这个非提权的关掉，避免两个启动器同时抢同一个监听端口。
            AppLifetime.Shutdown();
        }

        // ============================ 启停 ============================
        private async Task StartServiceAsync()
        {
            var key = CurrentKey.Trim();

            if (string.IsNullOrEmpty(key))
            {
                _notification.Warning(_isHostMode ? "请先填写或生成密钥" : "请填写房主给你的密钥", "提示");
                return;
            }

            IsBusy = true;
            try
            {
                PersistSettings();

                var error = await _easyTierService.StartAsync(key, _isHostMode, RelayServer);
                if (error != null)
                {
                    _notification.Error(error, "启动失败");
                    return;
                }

                _notification.Success(_isHostMode
                        ? "虚拟网络已启动。请进游戏执行「对局域网开放」，再把密钥和你的虚拟 IP 发给好友"
                        : "已加入虚拟网络。进游戏后在「多人游戏 → 直接连接」里填房主的虚拟 IP", "成功");
            }
            catch (Exception ex)
            {
                // 命令是 async void 派发过来的，异常漏出去会直接崩掉应用，这里必须兜住。
                Serilog.Log.Error(ex, "启动 EasyTier 联机服务时发生异常");
                _notification.Error($"联机服务启动出错：{ex.Message}", "启动失败");
            }
            finally
            {
                IsBusy = false;
                PullStateFromService();
            }
        }

        private void StopService()
        {
            try
            {
                _easyTierService.Stop();
                _notification.Show("联机服务已停止", "提示");
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "停止 EasyTier 联机服务时发生异常");
                _notification.Error($"停止联机服务出错：{ex.Message}", "停止失败");
            }
            finally
            {
                PullStateFromService();
            }
        }

        // ============================ 剪贴板 ============================

        private void CopyText(string? text)
        {
            var value = (text ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                _notification.Warning("还没有可复制的内容", "提示");
                return;
            }

            if (TrySetClipboard(value))
            {
                _notification.Success($"已复制：{value}", "成功");
            }
        }

        private async void PasteGuestKey()
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(AppLifetime.MainWindow)?.Clipboard;
                if (clipboard == null)
                    return;

                var pasted = await clipboard.TryGetTextAsync();
                if (string.IsNullOrWhiteSpace(pasted))
                    return;

                GuestKey = pasted.Trim();

                _notification.Show("已粘贴密钥", "提示");
            }
            catch (Exception ex)
            {
                _notification.Error($"读取剪贴板失败：{ex.Message}", "错误");
            }
        }

        private bool TrySetClipboard(string text)
        {
            try
            {
                TopLevel.GetTopLevel(AppLifetime.MainWindow)?.Clipboard?.SetTextAsync(text);
                return true;
            }
            catch (Exception ex)
            {
                _notification.Error($"复制失败：{ex.Message}", "错误");
                return false;
            }
        }

        // ============================ 状态同步 ============================

        private void OnServiceStateChanged()
        {
            var dispatcher = global::Avalonia.Threading.Dispatcher.UIThread;
            if (dispatcher == null)
                return;

            if (dispatcher.CheckAccess())
                PullStateFromService();
            else
                dispatcher.Post(PullStateFromService);
        }

        private void PullStateFromService()
        {
            RefreshCoreState();

            IsRunning = _easyTierService.IsRunning;
            LocalVirtualIp = _easyTierService.LocalVirtualIp;
            StatusMessage = _easyTierService.StatusMessage;

            var snapshot = _easyTierService.Peers;
            var fingerprint = string.Join("|", snapshot.Select(p =>
                $"{p.Ipv4}:{p.Hostname}:{p.Cost}:{p.Latency:0.#}:{p.IsSelf}"));

            if (fingerprint != _peerFingerprint)
            {
                _peerFingerprint = fingerprint;

                Peers.Clear();
                foreach (var peer in snapshot)
                    Peers.Add(peer);
            }

            RaiseDerivedState();
        }

        private void RaiseDerivedState()
        {
            OnPropertyChanged(nameof(IsNotRunning));
            OnPropertyChanged(nameof(HasLocalIp));
            OnPropertyChanged(nameof(HasNoPeers));
            OnPropertyChanged(nameof(HasRemotePeer));
            OnPropertyChanged(nameof(RemotePeerIp));
            OnPropertyChanged(nameof(CanStartService));
            OnPropertyChanged(nameof(CanStopService));
            OnPropertyChanged(nameof(CanEditKey));
            OnPropertyChanged(nameof(CanRegenerateKey));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusLevel));
            OnPropertyChanged(nameof(RoleBadge));
            OnPropertyChanged(nameof(CurrentKey));
        }

        // ============================ 设置持久化 ============================

        private void PersistKey()
        {
            SettingsService.Current.EasyTierNetworkKey = _hostKey;
            SettingsService.Save();
        }

        private void PersistSettings()
        {
            var settings = SettingsService.Current;
            settings.EasyTierRelayServer = _relayServer.Trim();
            settings.EasyTierNetworkKey = _hostKey.Trim();
            SettingsService.Save();
        }
    }
}
