using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Input;
using VibrantbitLauncher.Helpers;
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
    /// 两种身份在 EasyTier 层面做的是同一件事（用同一份凭据加入同一个虚拟网络），
    /// 差别只在提示文案和「谁去游戏里开房间」：
    ///   房主：生成联机码 → 启动服务 → 游戏里「对局域网开放」→ 把联机码 + 自己的虚拟 IP 发给好友
    ///   房客：填房主给的联机码 → 启动服务 → 在「多人游戏 → 直接连接」里填房主的虚拟 IP
    ///
    /// 联机码形如 <c>U/ABCD-EFGH-JKLM-NPQR</c>，由 <see cref="RoomCode"/> 解析成
    /// 网络名与网络密码两样东西 —— 串只有一份，凭据有两份，解析这一步就是两者的桥。
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
        private bool _isTerracottaCompat;

        /// <summary>退出启动器后是否保留联机内核（与设置同源，见 <see cref="KeepAliveOnExit"/>）。</summary>
        private bool _keepAliveOnExit = true;

        /// <summary>本次实际使用的中继地址文案（接口解析之后）。</summary>
        private string _relayEndpointText = string.Empty;

        // ===== 运行状态（由服务推送） =====
        private bool _isRunning;
        private string _localVirtualIp = string.Empty;
        private string _statusMessage = "未启动";
        private bool _isCoreInstalled;

        // ===== 局域网联机模组 =====
        private GameInstance? _selectedInstance;
        private bool _isInstallingLanMod;
        private string _lanModStatus = string.Empty;
        private string _lanModStatusLevel = "Idle";
        private string _lanModGuide = string.Empty;
        private bool _lanModInstalled;
        private string _lanModInstalledName = string.Empty;
        private string _lanModFileName = string.Empty;

        public MultiplayerPageViewModel(EasyTierService easyTierService)
        {
            _easyTierService = easyTierService;
            _easyTierService.StateChanged += OnServiceStateChanged;

            // 开关初值取设置（首次运行为 true）
            _keepAliveOnExit = SettingsService.Current.KeepMultiplayerAlive;

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
            DownloadCoreCommand = new RelayCommand(async () => await ShowInstallWindowAsync());
            RestartElevatedCommand = new RelayCommand(RestartElevated);
            InstallLanModCommand = new RelayCommand(async () => await InstallLanModAsync());
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

        /// <summary>给选中的实例一键安装「局域网联机模组」（关卡是加载器与游戏版本）。</summary>
        public RelayCommand InstallLanModCommand { get; }

        /// <summary>虚拟网络成员（含本机自己那一行）。</summary>
        public ObservableCollection<EasyTierPeer> Peers { get; } = new();

        // ============================ 局域网联机模组 ============================

        /// <summary>
        /// 本地所有可启动实例。
        ///
        /// 与「下载 → 模组 → 添加到实例」用的是同一份枚举（<see cref="GameInstanceHelper.GetInstances"/>），
        /// 因此加载器的判定口径完全一致 —— 不会出现「这里认成 Forge、模组页认成 Fabric」的割裂。
        /// </summary>
        public ObservableCollection<GameInstance> Instances { get; } = new();

        /// <summary>当前要给哪个实例装联机模组。</summary>
        public GameInstance? SelectedInstance
        {
            get => _selectedInstance;
            set
            {
                if (!SetProperty(ref _selectedInstance, value))
                    return;

                OnPropertyChanged(nameof(CanInstallLanMod));
                OnPropertyChanged(nameof(LanModDetailText));

                RefreshLanModState();

                if (value is not null)
                    PersistLanModInstance(value.Id);
            }
        }

        public bool HasInstances => Instances.Count > 0;

        public bool HasNoInstances => Instances.Count == 0;

        /// <summary>安装请求进行中（同时用来禁用按钮，避免连点触发两次下载）。</summary>
        public bool IsInstallingLanMod
        {
            get => _isInstallingLanMod;
            private set
            {
                if (!SetProperty(ref _isInstallingLanMod, value))
                    return;

                OnPropertyChanged(nameof(CanInstallLanMod));
                OnPropertyChanged(nameof(LanModButtonText));
            }
        }

        public bool CanInstallLanMod => !_isInstallingLanMod && _selectedInstance is not null;

        /// <summary>已装过联机模组（按钮文案会变成「重新安装」）。</summary>
        public bool LanModInstalled
        {
            get => _lanModInstalled;
            private set
            {
                if (!SetProperty(ref _lanModInstalled, value))
                    return;

                OnPropertyChanged(nameof(LanModButtonText));
                OnPropertyChanged(nameof(IsLanModMissing));
            }
        }

        public bool IsLanModMissing => !_lanModInstalled;

        public string LanModButtonText
        {
            get
            {
                if (_isInstallingLanMod) return "安装中…";
                return _lanModInstalled ? "重新安装" : "一键安装";
            }
        }

        /// <summary>状态行文案（未选实例 / 原版实例 / 已安装 / 待安装）。</summary>
        public string LanModStatusText
        {
            get => _lanModStatus;
            private set => SetProperty(ref _lanModStatus, value);
        }

        /// <summary>状态圆点：Ready = 已装好，Warn = 装不了，Busy = 进行中，Idle = 待安装。</summary>
        public string LanModStatusLevel
        {
            get => _lanModStatusLevel;
            private set => SetProperty(ref _lanModStatusLevel, value);
        }

        /// <summary>选中实例的加载器 + 游戏版本 + 落点，一行说明「这个模组会装到哪」。</summary>
        public string LanModDetailText
        {
            get
            {
                var instance = _selectedInstance;
                if (instance is null)
                    return "没有可用的实例";

                var version = InstanceIntegrityChecker.ResolveRootVersion(
                    GameInstanceHelper.GetMinecraftFolder(), instance.Id);

                var target = GameInstanceHelper.DescribeInstanceFolder(instance.Id, "mods");
                var isolation = GameInstanceHelper.IsVersionIsolated ? "版本隔离开" : "版本隔离关";

                return $"{instance.LoaderName} · MC {version} · {isolation} → {target}";
            }
        }

        /// <summary>装完之后怎么用（每个模组的联机方式不同，装完才显示）。</summary>
        public string LanModGuide
        {
            get => _lanModGuide;
            private set
            {
                if (SetProperty(ref _lanModGuide, value))
                    OnPropertyChanged(nameof(HasLanModGuide));
            }
        }

        public bool HasLanModGuide => !string.IsNullOrWhiteSpace(_lanModGuide);

        /// <summary>已装模组的文件名（界面上给出「装的是哪个 jar」，方便人工核对）。</summary>
        public string LanModFileName
        {
            get => _lanModFileName;
            private set => SetProperty(ref _lanModFileName, value);
        }

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

        /// <summary>房主侧的联机码：进页面自动生成，也可以手动改或重新生成。</summary>
        public string HostKey
        {
            get => _hostKey;
            set => SetProperty(ref _hostKey, value);
        }

        /// <summary>房客侧填的联机码（房主提供的那一串）。</summary>
        public string GuestKey
        {
            get => _guestKey;
            set => SetProperty(ref _guestKey, value);
        }

        /// <summary>EasyTier 中继服务器地址。由「陶瓦兼容模式」决定，也可以手动改。</summary>
        public string RelayServer
        {
            get => _relayServer;
            set => SetProperty(ref _relayServer, value);
        }

        /// <summary>
        /// 陶瓦联机兼容模式。
        ///
        /// <para>
        /// 勾上换用陶瓦联机的节点池 —— 联机码本来就是同一套算法，节点也对上之后，
        /// 用陶瓦客户端的玩家跟我们就进得同一张网。不勾则只走本项目自己的节点。
        /// </para>
        ///
        /// <para>
        /// 中继那一栏跟着它一起变，并且在这期间锁住不让手改：两个地址同时存在的话，
        /// 「到底连的是谁」就说不清了。
        /// </para>
        /// </summary>
        public bool IsTerracottaCompat
        {
            get => _isTerracottaCompat;
            set
            {
                if (!SetProperty(ref _isTerracottaCompat, value))
                    return;

                RelayServer = value ? RoomCode.TerracottaRelay : RoomCode.OwnRelay;
                RelayEndpointText = string.Empty;

                SettingsService.Current.EasyTierTerracottaCompat = value;
                PersistSettings();

                OnPropertyChanged(nameof(CanEditRelay));
            }
        }

        /// <summary>中继地址能不能手改：跑起来之后不能改，兼容模式下也不改（地址由模式决定）。</summary>
        public bool CanEditRelay => CanEditKey && !IsTerracottaCompat;

        /// <summary>
        /// 本次真正交给核心的中继地址。
        ///
        /// 中继那一栏填的可能是接口而不是服务器，界面上必须把「最后连的是谁」摆出来，
        /// 否则连不上时完全看不出问题出在接口没解析出来，还是一直在连一个死地址。
        /// </summary>
        public string RelayEndpointText
        {
            get => _relayEndpointText;
            private set
            {
                if (SetProperty(ref _relayEndpointText, value))
                    OnPropertyChanged(nameof(HasRelayEndpointText));
            }
        }

        public bool HasRelayEndpointText => !string.IsNullOrWhiteSpace(_relayEndpointText);

        /// <summary>
        /// 关掉启动器之后，联机内核是否继续在后台运行（默认开）。
        ///
        /// 关掉它意味着「退出启动器 = 断掉虚拟网络」，房主这边一关，房客会全部掉线；
        /// 开着则内核与启动器脱钩，窗口可以放心关掉/收进托盘。
        /// 改动即时写回设置，不需要再点一次保存。
        /// </summary>
        public bool KeepAliveOnExit
        {
            get => _keepAliveOnExit;
            set
            {
                if (!SetProperty(ref _keepAliveOnExit, value))
                    return;

                SettingsService.Current.KeepMultiplayerAlive = value;
                SettingsService.Save();
            }
        }

        // ============================ 联机码 ============================

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

        /// <summary>运行中和忙碌中都不给改联机码，免得核心实际用的和界面对不上。</summary>
        public bool CanEditKey => !IsBusy && !_isRunning;

        /// <summary>
        /// 能不能换身份。联机一旦起来就锁住：核心已经按「房主 / 房客」的身份连上网了，
        /// 这时候切一下只是改了界面上的单选框，底下的网络凭据和提示文案会跟实际状态错开。
        /// 停止服务后自动恢复。
        /// </summary>
        public bool CanSwitchRole => !IsBusy && !_isRunning;

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

            // 实例列表每次进页面都重新枚举：用户可能刚在「下载」页装了新版本或删了旧版本，
            // 缓存住只会让下拉框里躺着一堆已经不存在的实例。
            RefreshLanInstances();

            if (_isInitialized)
            {
                PullStateFromService();
                return;
            }

            _isInitialized = true;

            var saved = SettingsService.Current;

            // 中继不是自由发挥的一项：兼容模式开着就一定是陶瓦的节点，
            // 关着就用本项目的（老配置里那些历代默认值一律拽回当前默认）。
            _isTerracottaCompat = saved.EasyTierTerracottaCompat;
            var savedRelay = saved.EasyTierRelayServer?.Trim() ?? string.Empty;
            if (_isTerracottaCompat)
            {
                savedRelay = RoomCode.TerracottaRelay;
            }
            else if (savedRelay.Length == 0
                     || string.Equals(savedRelay, EasyTierService.LegacyDefaultRelayServer, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(savedRelay, EasyTierService.TransitionalDefaultRelayServer, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(savedRelay, EasyTierService.DefaultRelayServer, StringComparison.OrdinalIgnoreCase))
            {
                savedRelay = RoomCode.DefaultRelay;
            }

            _relayServer = savedRelay;
            OnPropertyChanged(nameof(RelayServer));
            OnPropertyChanged(nameof(IsTerracottaCompat));
            OnPropertyChanged(nameof(CanEditRelay));

            // 旧版本存的是 12 位随机密钥，读不出来就当作没有 —— 直接给一个新的联机码，
            // 免得把一个过期的、对面根本认不出的串摆在输入框里。
            if (RoomCode.TryParse(saved.EasyTierNetworkKey, out var savedCode, out _) && savedCode is not null)
            {
                _hostKey = savedCode.Code;
            }
            else
            {
                _hostKey = RoomCode.Generate();

                // 当场存下来：玩家很可能先把码发给好友，隔一会儿才点「启动服务」。
                // 不存的话中途重启启动器就换了一串，对方手里那串白记了。
                PersistKey();
            }

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

        // ============================ 局域网联机模组 ============================

        /// <summary>
        /// 重新枚举本地实例并恢复上次的选择。
        ///
        /// <para>
        /// 恢复优先级：本次页面的当前选择 → 配置文件里记的 id（用户上次选过的）→
        /// 第一个「装了加载器」的实例 → 第一个实例。
        /// 之所以不直接选第一个：原版实例装不了模组，默认落在它上面，用户点一下只会收到
        /// 「这是原版实例」的提示，等于默认状态就是「按钮没用」。
        /// </para>
        /// </summary>
        private void RefreshLanInstances()
        {
            var keepId = _selectedInstance?.Id;
            if (string.IsNullOrWhiteSpace(keepId))
                keepId = SettingsService.Current.LanModInstanceId;

            var instances = GameInstanceHelper.GetInstances();

            Instances.Clear();
            foreach (var instance in instances)
                Instances.Add(instance);

            OnPropertyChanged(nameof(HasInstances));
            OnPropertyChanged(nameof(HasNoInstances));

            GameInstance? pick = null;
            if (!string.IsNullOrWhiteSpace(keepId))
            {
                pick = Instances.FirstOrDefault(x =>
                    string.Equals(x.Id, keepId, StringComparison.OrdinalIgnoreCase));
            }

            pick ??= Instances.FirstOrDefault(x =>
                        x.Loader is not (ModLoaderKind.Vanilla or ModLoaderKind.Unknown))
                    ?? Instances.FirstOrDefault();

            // ItemsSource 被清空时 ComboBox 会把 SelectedItem 写回 null，
            // 所以赋值必须放在重建集合之后。
            SelectedInstance = pick;

            if (pick is null)
            {
                LanModInstalled = false;
                LanModFileName = string.Empty;
                LanModStatusText = "还没有安装任何 Minecraft 实例，请先在「下载」页装一个版本";
                LanModStatusLevel = "Warn";
            }
            else
            {
                // 选中的实例没变时 setter 不会重跑（SetProperty 判定值相同），
                // 但 mods 目录可能刚被「下载 → 模组」页改过，这里补一次无条件刷新。
                RefreshLanModState();
            }
        }

        /// <summary>按「选中的实例 + 它当前 mods 目录里的文件」刷新状态行。</summary>
        private void RefreshLanModState()
        {
            if (_isInstallingLanMod)
                return;

            var instance = _selectedInstance;
            if (instance is null)
            {
                LanModInstalled = false;
                LanModFileName = string.Empty;
                LanModStatusText = "没有选择实例";
                LanModStatusLevel = "Warn";
                OnPropertyChanged(nameof(LanModDetailText));
                return;
            }

            var state = LanModInstaller.Detect(instance.Id);
            LanModInstalled = state.IsInstalled;
            LanModFileName = state.FileName;

            if (state.IsInstalled)
            {
                // 历史实例里可能躺着手动装的 e4mc / LAN Server Properties。
                // 不拦着它们用，但要明确告诉玩家「不是推荐的那个，可以一键替换」。
                LanModStatusText = state.Kind == LanModKind.McWifiPnp
                    ? $"已安装 {state.DisplayName}，开房后按下面的说明操作即可"
                    : $"已安装 {state.DisplayName}（旧方案），点一键安装会换成 LAN World Plug-n-Play";

                LanModStatusLevel = "Ready";
            }
            else
            {
                switch (instance.Loader)
                {
                    case ModLoaderKind.Vanilla:
                        LanModStatusText = "这个实例是原版，没有加载器 —— 模组装进去也不会被加载";
                        LanModStatusLevel = "Warn";
                        break;

                    case ModLoaderKind.Unknown:
                        LanModStatusText = "读不出这个实例的加载器（版本 JSON 可能损坏），建议先做一次体检";
                        LanModStatusLevel = "Warn";
                        break;

                    case ModLoaderKind.LiteLoader:
                        LanModStatusText = "LiteLoader 没有可用的局域网联机模组";
                        LanModStatusLevel = "Warn";
                        break;

                    default:
                        LanModStatusText = $"尚未安装（{instance.LoaderName} 实例，可以一键装好）";
                        LanModStatusLevel = "Idle";
                        break;
                }
            }

            OnPropertyChanged(nameof(LanModDetailText));
        }

        /// <summary>
        /// 一键安装。真正的加载器 / 版本判定在 <see cref="LanModInstaller"/> 里做，
        /// 这里只负责「置忙 → 调用 → 汇报 → 刷新状态」。
        /// </summary>
        private async Task InstallLanModAsync()
        {
            if (_isInstallingLanMod)
                return;

            var instance = _selectedInstance;
            if (instance is null)
            {
                _notification.Warning("请先从下拉框选择一个实例", "提示");
                return;
            }

            IsInstallingLanMod = true;
            LanModStatusLevel = "Busy";
            LanModStatusText = "正在准备…";
            LanModGuide = string.Empty;

            try
            {
                // 进度回调在后台线程发出来，绑定属性必须在 UI 线程改
                var result = await LanModInstaller.InstallAsync(instance, text =>
                    Dispatcher.UIThread.Post(() => LanModStatusText = text));

                if (result.Success)
                {
                    _notification.Success(result.Message, $"{result.Title} 已安装");
                    LanModGuide = result.Guide;
                }
                else
                {
                    _notification.Warning(result.Message, "装不了联机模组");
                }
            }
            catch (OperationCanceledException)
            {
                _notification.Show("已取消安装", "提示");
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "安装局域网联机模组时发生异常");
                _notification.Error($"安装联机模组出错：{ex.Message}", "失败");
            }
            finally
            {
                IsInstallingLanMod = false;
                RefreshLanModState();
            }
        }

        /// <summary>记住用户选的实例，下次进来直接恢复。</summary>
        private void PersistLanModInstance(string instanceId)
        {
            if (string.Equals(SettingsService.Current.LanModInstanceId, instanceId, StringComparison.Ordinal))
                return;

            SettingsService.Current.LanModInstanceId = instanceId;
            SettingsService.Save();
        }

        private void RegenerateKey()
        {
            HostKey = RoomCode.Generate();
            PersistKey();
            _notification.Show($"已生成新联机码：{HostKey}", "提示");
        }

        /// <summary>
        /// 打开「安装联机模块」子页，等它退场后再重新探测核心。
        ///
        /// <para>
        /// 这里必须 <c>await</c>：内嵌子页是异步打开的，安装过程全在它里面进行。
        /// 早先的写法是「打开后立刻刷新」，那一刻连下载都还没开始，查出来的当然是「没装」，
        /// 于是装完关掉子页，界面还挂在「未找到 EasyTier 核心」上，只有切走再切回才恢复。
        /// </para>
        /// </summary>
        private async Task ShowInstallWindowAsync()
        {
            try
            {
                await AppLifetime.ShowDialogAsync(new InstallEasyTierWindow());
            }
            catch (Exception ex)
            {
                _notification.Error($"打开下载窗口失败：{ex.Message}", "错误");
                return;
            }

            // 走到这里说明子页已经退场：装完了，或者用户中途退出。
            // 两种都重新探一次 —— 文件在不在，查一下就知道。
            RefreshCoreState();

            if (_isCoreInstalled)
                _notification.Success("EasyTier 核心已就绪，现在可以启动服务了", "成功");
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
            var raw = CurrentKey;

            if (string.IsNullOrWhiteSpace(raw))
            {
                _notification.Warning(_isHostMode ? "请先填写或生成联机码" : "请填写房主给你的联机码", "提示");
                return;
            }

            // 先把码解成「网络名 + 网络密码」。校验不过就不要往下走 ——
            // 抄错一位也能进一张「存在但没人」的网，玩家会在游戏里白等十几分钟。
            if (!RoomCode.TryParse(raw, out var code, out var parseError) || code is null)
            {
                _notification.Warning(parseError ?? "联机码无效。", "联机码不对");
                return;
            }

            IsBusy = true;
            try
            {
                // 回填规范化后的码（大写、补齐连字符），方便双方核对是不是同一串
                if (_isHostMode)
                    HostKey = code.Code;
                else
                    GuestKey = code.Code;

                PersistSettings();

                // 中继那一栏填的可能不是服务器，而是一个「发地址的接口」（陶瓦的公共节点就是这样）：
                // 真的节点会换 IP 换端口，所以得先问接口要一眼当下的地址，再把地址交给核心。
                // 这步放在起核心之前 —— 一个地址都拿不到就不必起，省得核心挂着干等。
                var chain = new List<string> { RelayServer };
                if (RoomCode.ExtraRelaysFor(RelayServer) is { } extras)
                    chain.AddRange(extras);

                var relays = new List<string>(await PublicRelayResolver.ResolveAllAsync(chain));

                if (relays.Count == 0)
                {
                    // 接口一个地址都没拿到，但裸的直连节点也没有（多半是玩家自己填的接口地址）。
                    // 这时候还是把接口地址原样交给核心：它有自己的 TLS 栈和重试，
                    // 我们这边取不到，不代表它取不到 —— 总比直接报错让玩家连不了强。
                    relays.Add(RelayServer);
                }

                var error = await _easyTierService.StartAsync(
                    code.NetworkName, code.NetworkSecret, _isHostMode,
                    relays[0], KeepAliveOnExit, relays.Skip(1).ToArray());

                if (error != null)
                {
                    RelayEndpointText = string.Empty;
                    _notification.Error(error, "启动失败");
                    return;
                }

                RelayEndpointText = relays[0].StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? $"{relays[0]}（暂时没取到真实地址，已交给核心自己去取）"
                    : relays.Count > 1
                        ? $"本次实际使用 {relays[0]}，另有 {relays.Count - 1} 个备用节点"
                        : $"本次实际使用 {relays[0]}";

                _notification.Success(_isHostMode
                        ? "虚拟网络已启动。请进游戏执行「对局域网开放」，再把联机码和你的虚拟 IP 发给好友"
                        : "已加入虚拟网络。进游戏后在「多人游戏 → 直接连接」里填房主的虚拟 IP", "成功");
            }
            catch (Exception ex)
            {
                // 命令是 async void 派发过来的，异常漏出去会直接崩掉应用，这里必须兜住。
                Serilog.Log.Error(ex, "启动 EasyTier 联机服务时发生异常");
                RelayEndpointText = string.Empty;
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
                RelayEndpointText = string.Empty;
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

                _notification.Show("已粘贴联机码", "提示");
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
            OnPropertyChanged(nameof(CanEditRelay));
            OnPropertyChanged(nameof(CanSwitchRole));
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
            settings.EasyTierTerracottaCompat = _isTerracottaCompat;
            settings.EasyTierNetworkKey = _hostKey.Trim();
            SettingsService.Save();
        }
    }
}
