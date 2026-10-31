using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using FluentAvalonia.Styling;
using MinecraftLaunch.Base.Models.Authentication;
using MinecraftLaunch.Base.Models.Authentication.Yggdrasil;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.ViewModels.Windows;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 应用程序设置数据模型，序列化为 JSON 配置文件。
    /// 账户直接使用 MinecraftLaunch 的类型，不做二次封装。
    /// </summary>
    public class AppSettings
    {
        public string Theme { get; set; } = "Light";
        public string AccentColor { get; set; } = "#FF2F6BF0";
        /// <summary>主题渐变色的终点色（历史字段，1.0.27.0 起界面不再提供渐变，保留以免旧配置读不出来）。</summary>
        public string AccentColorEnd { get; set; } = "#FF2F6BF0";
        /// <summary>是否启用「主题渐变色」。历史字段，新设计固定为纯色，不再生效。</summary>
        public bool UseAccentGradient { get; set; }
        /// <summary>背景图路径：本地绝对路径，或内置壁纸的资源 URI；空表示不使用背景图。</summary>
        public string BackgroundImagePath { get; set; } = string.Empty;
        /// <summary>背景图显示不透明度。</summary>
        public double BackgroundImageOpacity { get; set; } = 0.85;
        /// <summary>日志最低记录等级：Verbose / Debug / Information / Warning / Error / Fatal。</summary>
        public string LogLevel { get; set; } = "Information";
        /// <summary>日志文件保留天数，超期自动清理。</summary>
        public int LogRetentionDays { get; set; } = 7;
        /// <summary>是否同时把日志写到调试器输出。</summary>
        public bool LogToDebugOutput { get; set; } = true;
        public string MinecraftFolder { get; set; } = "./.minecraft";
        public string JavaPath { get; set; } = string.Empty;

        /// <summary>
        /// Java 安装根目录（用户在「Java 下载」页选的那个）。
        ///
        /// <para>
        /// 以前这个路径只活在页面上的输入框里：装到 D:\Java 之后，
        /// 下载页自己能扫到（它扫输入框），设置页 / 启动页却看不见 ——
        /// 因为它们走的是 <c>JavaHelper.EnumerateCandidates</c> 的固定候选目录。
        /// 用户看到的就是「装完检测不到」。持久化并纳入扫描范围后两边才一致。
        /// </para>
        ///
        /// <para>空字符串表示用默认值（<c>%ProgramFiles%\Eclipse Adoptium</c>）。</para>
        /// </summary>
        public string JavaRuntimeRoot { get; set; } = string.Empty;

        // ===== 启动 · Java 虚拟机与内存 =====
        /// <summary>最大堆内存（MB），对应 JVM 的 -Xmx。</summary>
        public int MaxMemoryMb { get; set; } = 2048;
        /// <summary>初始堆内存（MB），对应 JVM 的 -Xms。</summary>
        public int MinMemoryMb { get; set; } = 512;
        /// <summary>用户追加的 JVM 参数，一行一条，原样拼在 -cp 之前。</summary>
        public string JvmArgs { get; set; } = string.Empty;

        // ===== 启动 · 游戏目录 =====
        /// <summary>版本隔离：每个版本使用独立的游戏目录（saves / mods / config 等）。</summary>
        public bool VersionIsolation { get; set; } = true;

        // ===== 启动 · 高级 =====
        /// <summary>禁用 IPv6（加 -Djava.net.preferIPv4Stack=true）。IPv6 环境连不上服务器时开启。</summary>
        public bool DisableIpv6 { get; set; }
        /// <summary>Java 垃圾回收器：Default / G1GC / ZGC / Parallel / Serial。</summary>
        public string GcMode { get; set; } = "Default";
        /// <summary>游戏窗口宽度（同时用于 ${resolution_width} 占位符与 --width）。</summary>
        public int WindowWidth { get; set; } = 854;
        /// <summary>游戏窗口高度。</summary>
        public int WindowHeight { get; set; } = 480;
        /// <summary>是否以全屏启动游戏。</summary>
        public bool StartFullscreen { get; set; }

        // ===== 个性化 · 主界面 =====
        /// <summary>启动游戏后对启动器自身执行的动作：None / Minimize / Close。</summary>
        public string AfterLaunchAction { get; set; } = "None";

        /// <summary>
        /// 性能模式：为低配电脑（尤其是 Windows 7 x86 老机器）关闭一切「好看但费性能」的效果。
        ///
        /// 关闭项：Mica 云母材质、壁纸背景、页面切换过渡、控件悬停 / 按下动画、卡片阴影、
        /// 平滑滚动、高质量位图缩放；同时把渲染后端限制为软件渲染，回避老显卡驱动上的
        /// D3D 兼容问题（那类问题上表现为白屏 / 黑屏 / 严重卡顿）。
        /// </summary>
        public bool PerformanceMode { get; set; }

        /// <summary>用户是否已被询问过「要不要开启性能模式」，避免每次启动都弹。</summary>
        public bool PerformanceModeAsked { get; set; }

        // ===== 通用 · 主页显示项 =====
        // 「主页」指启动页（导航里的「启动」）。这些开关只影响那一页的模块可见性，
        // 关掉任何一个都不会让功能消失 —— 对应功能在别的页面仍然可达。
        public bool ShowHomeGreeting { get; set; } = true;
        public bool ShowHomeLaunchCard { get; set; } = true;
        public bool ShowHomeRecent { get; set; } = true;
        public bool ShowHomeInstanceOverview { get; set; } = true;
        public bool ShowHomeShortcuts { get; set; } = true;

        /// <summary>启动时自动恢复上次登录的账户（见 <see cref="AccountRestorer"/>）。</summary>
        public bool AutoLoginLastAccount { get; set; } = true;

        /// <summary>检查更新时是否包含测试版（pre-release）发布。</summary>
        public bool UpdateIncludePrerelease { get; set; }

        // ===== 游戏 · 启动参数 =====
        /// <summary>是否追加用户自定义的游戏参数（写在 mojang 的 game 参数之后）。</summary>
        public bool UseCustomGameArgs { get; set; }
        /// <summary>自定义游戏参数，一行一条。</summary>
        public string GameArgs { get; set; } = string.Empty;

        /// <summary>资源包目录的自定义位置；空表示用游戏目录下的 resourcepacks。</summary>
        public string ResourcePacksFolder { get; set; } = string.Empty;
        /// <summary>存档目录的自定义位置；空表示用游戏目录下的 saves。</summary>
        public string SavesFolder { get; set; } = string.Empty;

        // ===== 游戏 · 存档备份 =====

        /// <summary>
        /// 启动游戏前自动把该实例的存档打包一份。
        ///
        /// <para>
        /// 默认开：存档是玩家唯一不能重下的东西，而「启动器把存档弄坏了」是最难解释的故障。
        /// 关掉的人通常是自己已经有别的备份手段（或者磁盘真的紧张）。
        /// </para>
        /// </summary>
        public bool AutoBackupSaves { get; set; } = true;

        /// <summary>每个实例保留几份备份，超出删最旧的。</summary>
        public int SaveBackupKeepCount { get; set; } = 5;

        /// <summary>
        /// 内存分配是否为「自动推荐」。
        ///
        /// 开启时 <see cref="MaxMemoryMb"/> 由本机内存推算（一半向下取档），
        /// 用户拖滑块即自动切到手动。
        /// </summary>
        public bool MemoryAutoRecommend { get; set; } = true;

        // ===== 外观 · 动效 =====
        /// <summary>动画档位：Full（完整）/ Reduced（精简）/ Off（关闭）。</summary>
        public string AnimationLevel { get; set; } = "Full";

        /// <summary>页面切换方式：Slide（滑动）/ Fade（渐变）/ None（无）。</summary>
        public string PageTransition { get; set; } = "Slide";

        /// <summary>动画速度倍率，0.5（慢）~ 2（快），1 为基准。</summary>
        public double AnimationSpeed { get; set; } = 1.0;

        // ===== 外观 · 语言与字体 =====
        /// <summary>
        /// 界面语言：<c>zh-CN</c> / <c>en-US</c>；<b>空表示跟随系统</b>。
        ///
        /// 默认给空串而不是 <c>zh-CN</c>：新装的用户该按系统语言走，
        /// 老配置里这一项已经写着具体值，不受影响。
        /// </summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>界面字体族名；空表示跟随系统默认。</summary>
        public string FontFamily { get; set; } = string.Empty;

        /// <summary>字体缩放倍率，0.8 ~ 1.5。</summary>
        public double FontScale { get; set; } = 1.0;

        /// <summary>高对比度模式：把正文与次要文字拉到纯黑 / 纯白。</summary>
        public bool HighContrast { get; set; }

        // ===== 外观 · 背景 =====
        /// <summary>背景模式：None（无）/ Color（纯色）/ Image（图片或内置壁纸）。</summary>
        public string BackgroundMode { get; set; } = "Image";

        /// <summary>纯色背景的颜色（仅 BackgroundMode 为 Color 时生效）。</summary>
        public string BackgroundColor { get; set; } = "#FF12161C";

        /// <summary>毛玻璃强度 0~1：作用在背景图上，越大越模糊。</summary>
        public double BackgroundBlur { get; set; }

        /// <summary>暗角强度 0~1：向四角压暗，突出中间内容。</summary>
        public double BackgroundVignette { get; set; }

        /// <summary>背景图是否缓慢缩放（"动态"背景）。</summary>
        public bool BackgroundAnimated { get; set; }

        // ===== 高级 · 诊断 =====
        /// <summary>崩溃日志是否自动上传（目前仅记录到本地日志，保留开关位）。</summary>
        public bool CrashReportAutoUpload { get; set; }

        // ===== 网络 · 下载 =====
        /// <summary>
        /// 下载源策略：Auto（自动测速，镜像优先并允许官方兜底）/
        /// Mirror（固定镜像，镜像不可用也不回退）/ Official（官方优先）。
        /// </summary>
        public string DownloadSourceMode { get; set; } = "Auto";

        /// <summary>单次下载的总速度上限（KB/s，0 表示不限）。</summary>
        public int DownloadSpeedLimitKb { get; set; }

        /// <summary>单个 HTTP 请求的超时秒数。</summary>
        public int DownloadTimeoutSeconds { get; set; } = 30;

        /// <summary>使用 BMCLAPI 镜像下载（国内加速）。</summary>
        public bool UseBmclMirror { get; set; }

        /// <summary>
        /// 同时下载的文件数（批量下载的外层并发），1~16。
        ///
        /// <para>
        /// 和 <see cref="DownloadChunkCount"/> 是**两个维度**：
        /// 总连接数 ≈ 并发 × 分块。原先把两者混成一个「下载线程数」，
        /// 结果是想加速单个大文件时，顺手把并发文件数也一起拉高了 —— 镜像端直接 403。
        /// </para>
        /// </summary>
        public int DownloadConcurrency { get; set; } = 6;

        /// <summary>
        /// 单个文件切成几块并行下载（内层并发连接），1~16。1 表示不分块、走单连接。
        /// </summary>
        public int DownloadChunkCount { get; set; } = 4;

        /// <summary>
        /// [已废弃] 1.0.9.7 及更早版本的「下载线程数」。
        ///
        /// <para>
        /// 保留这个字段只为读取旧配置做一次性迁移（见
        /// <c>MigrateLegacyDownloadSettings</c>），迁移后置 0。
        /// **新代码不要再读它**，否则又回到「一个值管两件事」的老问题。
        /// </para>
        /// </summary>
        public int DownloadThreads { get; set; }

        /// <summary>下载失败重试次数。</summary>
        public int DownloadRetryCount { get; set; } = 4;

        // ===== 联机（EasyTier） =====
        /// <summary>EasyTier 中继节点地址，默认用本项目自己的节点。</summary>
        public string EasyTierRelayServer { get; set; } = "tcp://easytier.weiai.org.cn:11010";

        /// <summary>
        /// 陶瓦联机兼容模式（默认关）。
        ///
        /// <para>
        /// 勾上就改用陶瓦联机的节点池。网络名 / 密码本来就是同一套算法，
        /// 所以只要节点也对上，两边同一个联机码就能进同一张网 —— 用陶瓦客户端的玩家也能进来。
        /// 关掉则只走本项目自己的节点。
        /// </para>
        /// </summary>
        public bool EasyTierTerracottaCompat { get; set; }

        /// <summary>上次房主侧使用的联机密钥（同时作为网络名与网络密码）。</summary>
        public string EasyTierNetworkKey { get; set; } = string.Empty;

        /// <summary>
        /// 联机页「局域网联机模组」上次选中的实例 id。
        /// 存 id 而不是索引 —— 实例会被装、被删、被改名，索引下次就对不上了。
        /// </summary>
        public string LanModInstanceId { get; set; } = string.Empty;

        /// <summary>
        /// 关闭启动器后是否让 EasyTier 联机内核继续运行（默认开）。
        ///
        /// 房主把游戏挂在后台去干别的事时，启动器一关就把虚拟网卡拆掉，房客那边会
        /// 直接掉线 —— 所以默认改成「跟启动器脱钩」。下次启动器启动时会自动接管
        /// 这个还在跑的内核（见 <c>EasyTierService.TryRestoreSessionAsync</c>），
        /// 密钥 / 身份 / 虚拟 IP 都沿用，不需要重新加入。
        /// </summary>
        public bool KeepMultiplayerAlive { get; set; } = true;

        /// <summary>
        /// 点窗口右上角关闭时收进系统托盘而不是退出程序（默认开）。
        /// 真正的退出走托盘菜单里的「退出启动器」。
        /// </summary>
        public bool MinimizeToTray { get; set; } = true;

        public List<MicrosoftAccount> MicrosoftAccounts { get; set; } = new();
        public List<YggdrasilAccount> YggdrasilAccounts { get; set; } = new();
        public List<OfflineAccount> OfflineAccounts { get; set; } = new();
        public string SelectedAccountUuid { get; set; } = string.Empty;
        public bool IsMicrosoftAccount { get; set; }
        public bool IsFirstRun { get; set; } = true;
    }

    /// <summary>
    /// 设置配置文件服务：负责加载、保存和应用设置。
    /// </summary>
    public static class SettingsService
    {
        private static readonly string ConfigPath =
            Path.Combine(AppContext.BaseDirectory, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static AppSettings Current { get; private set; } = new();

        /// <summary>
        /// 配置文件读写的互斥锁。
        /// Save() 会被 UI 线程（改设置）和后台线程（安装完成、令牌刷新后保存账户）同时调用，
        /// 没有保护地并发写同一个文件会写出半截 JSON。
        /// </summary>
        private static readonly object ConfigLock = new();

        /// <summary>
        /// 取当前生效的 .minecraft 目录，**始终返回绝对路径**。
        ///
        /// 配置里为了可移植性默认存的是相对路径 "./.minecraft"，直接显示给用户
        /// （尤其在设置页的只读输入框里）会让人摸不着头脑。这里统一规范成绝对路径，
        /// 既便于界面展示，也避免各处再各自 Path.GetFullPath。
        /// </summary>
        public static string ResolveMinecraftFolder()
        {
            var folder = MainWindowViewModel.MainModel.MinecraftFolder;
            if (string.IsNullOrWhiteSpace(folder))
                folder = DefaultMinecraftFolder;

            try
            {
                return Path.GetFullPath(folder);
            }
            catch
            {
                // 路径非法（含非法字符等）时退回默认目录的绝对路径，绝不让界面崩
                return Path.GetFullPath(DefaultMinecraftFolder);
            }
        }

        /// <summary>默认的 .minecraft 目录（相对路径，作为配置的初始值以保持可移植）。</summary>
        private const string DefaultMinecraftFolder = "./.minecraft";

        public static void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    // 有些编辑器 / 脚本会写入 UTF-8 BOM，JsonSerializer 不接受，这里显式去掉
                    var json = File.ReadAllText(ConfigPath).TrimStart('\uFEFF');
                    Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

                    // 老配置没有「下载源策略」这个字段：按原来的镜像开关推断，
                    // 否则关过镜像的人升级后会被默默改回镜像优先。
                    if (!json.Contains("\"DownloadSourceMode\"", StringComparison.Ordinal))
                        Current.DownloadSourceMode = Current.UseBmclMirror ? "Auto" : "Official";
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "配置文件解析失败，已回退到默认设置：{Path}", ConfigPath);
                Current = new AppSettings();
            }

            MainWindowViewModel.MainModel.MinecraftFolder = Current.MinecraftFolder;
            MainWindowViewModel.MainModel.JavaPath = string.IsNullOrEmpty(Current.JavaPath) ? null : Current.JavaPath;
            MainWindowViewModel.MainModel.IsMicrosoftAccount = Current.IsMicrosoftAccount;

            // 旧配置的「下载线程数」拆成「并发文件数 + 分块数」（1.0.9.8）
            MigrateLegacyDownloadSettings();
        }

        /// <summary>
        /// 把 1.0.9.7 及更早的单一「下载线程数」拆成两个独立设置。
        ///
        /// <para>
        /// 旧值同时被「批量下载的文件并发」和「单文件分块数」两个地方使用，
        /// 语义本身是矛盾的（10 线程既当 10 个文件并发、又被 clamp 成 8 块）。
        /// 迁移时两个新字段都先沿用旧值，至少不会让升级后的下载速度突然变慢，
        /// 用户随后可以在设置页分别调。
        /// </para>
        ///
        /// <para>
        /// 用「旧字段 &gt; 0」作为「尚未迁移」的标记，迁移后置 0 ——
        /// JSON 里留着 0，下次启动不会重复迁移。
        /// </para>
        /// </summary>
        private static void MigrateLegacyDownloadSettings()
        {
            var legacy = Current.DownloadThreads;
            if (legacy <= 0)
                return;

            Current.DownloadConcurrency = Math.Clamp(legacy, 1, 16);
            Current.DownloadChunkCount = Math.Clamp(legacy, 1, 16);
            Current.DownloadThreads = 0;

            Serilog.Log.Information(
                "下载设置已迁移：旧「线程数 {Legacy}」→ 并发文件数 {Concurrency} + 单文件分块数 {Chunks}",
                legacy, Current.DownloadConcurrency, Current.DownloadChunkCount);

            Save();
        }

        public static void Save()
        {
            lock (ConfigLock)
            {
                try
                {
                    Current.MinecraftFolder = MainWindowViewModel.MainModel.MinecraftFolder ?? "./.minecraft";
                    Current.JavaPath = MainWindowViewModel.MainModel.JavaPath ?? string.Empty;
                    Current.IsMicrosoftAccount = MainWindowViewModel.MainModel.IsMicrosoftAccount;

                    // 新旧两个镜像字段保持同步：UseBmclMirror 仍被下载链路直接读取，
                    // 口径统一由 DownloadSourceMode 决定，避免两处各说各话。
                    Current.UseBmclMirror = DownloadSourceMode != "Official";

                    // 先写临时文件再原子替换：写到一半被打断也不会留下半截 JSON
                    var json = JsonSerializer.Serialize(Current, JsonOptions);
                    var tempPath = ConfigPath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, ConfigPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "配置保存失败：{Path}", ConfigPath);
                }
            }
        }

        // ===== 账户保存（直接使用 MinecraftLaunch 类型） =====

        public static void SaveMicrosoftAccount(MicrosoftAccount account)
        {
            var existing = Current.MicrosoftAccounts.FirstOrDefault(a => a.Uuid == account.Uuid);
            if (existing != null)
                Current.MicrosoftAccounts.Remove(existing);
            Current.MicrosoftAccounts.Add(account);
            Save();
        }

        public static void SaveYggdrasilAccount(YggdrasilAccount account)
        {
            var existing = Current.YggdrasilAccounts.FirstOrDefault(a => a.Uuid == account.Uuid);
            if (existing != null)
                Current.YggdrasilAccounts.Remove(existing);
            Current.YggdrasilAccounts.Add(account);
            Save();
        }

        public static void SaveOfflineAccount(OfflineAccount account)
        {
            var existing = Current.OfflineAccounts.FirstOrDefault(a => a.Name == account.Name);
            if (existing != null)
                Current.OfflineAccounts.Remove(existing);
            Current.OfflineAccounts.Add(account);
            Save();
        }

        public static void SetSelectedAccount(string uuid)
        {
            Current.SelectedAccountUuid = uuid ?? string.Empty;
            Save();
        }

        public static void RemoveMicrosoftAccount(Guid uuid)
        {
            var existing = Current.MicrosoftAccounts.FirstOrDefault(a => a.Uuid == uuid);
            if (existing != null)
                Current.MicrosoftAccounts.Remove(existing);
            Save();
        }

        public static void RemoveYggdrasilAccount(Guid uuid)
        {
            var existing = Current.YggdrasilAccounts.FirstOrDefault(a => a.Uuid == uuid);
            if (existing != null)
                Current.YggdrasilAccounts.Remove(existing);
            Save();
        }

        public static void RemoveOfflineAccount(string name)
        {
            var existing = Current.OfflineAccounts.FirstOrDefault(a => a.Name == name);
            if (existing != null)
                Current.OfflineAccounts.Remove(existing);
            Save();
        }

        /// <summary>根据账户类型自动删除。</summary>
        public static void RemoveAccount(Account account)
        {
            switch (account)
            {
                case MicrosoftAccount ms:
                    RemoveMicrosoftAccount(ms.Uuid);
                    break;
                case YggdrasilAccount yg:
                    RemoveYggdrasilAccount(yg.Uuid);
                    break;
                case OfflineAccount off:
                    RemoveOfflineAccount(off.Name);
                    break;
            }
        }

        // ===== 下载（下载源 / 重试次数） =====

        /// <summary>
        /// 取「同时下载的文件数」，并夹到 [1, <paramref name="max"/>]。
        ///
        /// <para>
        /// 各下载路径对并发的容忍度不同：原版依赖清单动辄几百个文件，可以放到 16；
        /// Fabric / Forge 的库只有十几个，实测并发 10 路打 BMCLAPI 会立刻 403，
        /// 所以那些地方调用时传更小的 <paramref name="max"/>。
        /// </para>
        /// </summary>
        /// <param name="max">该路径允许的上限。</param>
        public static int ResolveDownloadConcurrency(int max = 16)
        {
            var value = Current.DownloadConcurrency;
            if (value <= 0)
                value = 6;

            return Math.Clamp(value, 1, Math.Max(1, max));
        }

        /// <summary>
        /// 把下载相关设置应用到 MinecraftLaunch。
        ///
        /// 实测 <c>InitializeHelper.Initialize</c> 内部只是把参数写进 <c>DownloadManager</c> 的静态属性，
        /// 可反复调用且立即生效（镜像开关会立刻影响 <c>DownloadManager.BmclApi.TryFindUrl</c> 的改写结果），
        /// 所以设置页改完直接调一次即可，无需重启。
        ///
        /// 注意 BMCLAPI 的改写范围：launchermeta / piston-meta / libraries / resources / launcher.mojang.com
        /// 会被换成 bmclapi2.bangbang93.com，但 <c>piston-data.mojang.com</c> 不在其覆盖内
        /// （部分版本的 client.jar 仍走官方源）。
        ///
        /// 注意这里管不到本工程自己的下载器（<c>Services/Download/</c>）——
        /// 那条链路在每次下载时实时读 <see cref="AppSettings.DownloadConcurrency"/> 与
        /// <see cref="AppSettings.DownloadChunkCount"/>，不需要重新 Initialize。
        /// </summary>
        /// <summary>下载源策略的规范值：Auto / Mirror / Official。</summary>
        public static string DownloadSourceMode =>
            Current.DownloadSourceMode is "Auto" or "Mirror" or "Official"
                ? Current.DownloadSourceMode
                : "Auto";

        public static void ApplyDownloadSettings()
        {
            try
            {
                MinecraftLaunch.InitializeHelper.Initialize(settings =>
                {
                    settings.MaxThread = 256;
                    settings.MaxFragment = 128;
                    settings.MaxRetryCount = Math.Clamp(Current.DownloadRetryCount, 0, 10);
                    // 「固定官方源」才真的关掉镜像改写；自动 / 固定镜像都允许走镜像
                    settings.IsEnableMirror = DownloadSourceMode != "Official";
                    settings.IsEnableFragment = false;
                });

                Serilog.Log.Information("下载设置：下载源={Source} 重试={Retry} 次 限速={Limit}",
                    DownloadSourceMode switch
                    {
                        "Mirror" => "固定镜像",
                        "Official" => "官方优先",
                        _ => "自动测速"
                    },
                    Current.DownloadRetryCount,
                    Current.DownloadSpeedLimitKb > 0 ? $"{Current.DownloadSpeedLimitKb} KB/s" : "不限");
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "应用下载设置失败");
            }
        }

        // ===== 主题 / 主题色 / 背景图 =====

        /// <summary>默认主题色，配置缺失或解析失败时回退到它。</summary>
        public static readonly Color DefaultAccentColor = Color.FromRgb(0x00, 0x78, 0xD4);

        private static bool IsDarkTheme =>
            CurrentThemeVariant == ThemeVariant.Dark;

        /// <summary>是否「跟随系统」明暗。</summary>
        public static bool FollowSystemTheme =>
            Current.Theme.Equals("System", StringComparison.OrdinalIgnoreCase)
            || Current.Theme.Equals("FollowSystem", StringComparison.OrdinalIgnoreCase);

        /// <summary>系统当前的明暗（拿不到平台信息时按亮色处理）。</summary>
        private static ThemeVariant SystemThemeVariant
        {
            get
            {
                try
                {
                    var variant = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
                    return variant == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
                }
                catch
                {
                    return ThemeVariant.Light;
                }
            }
        }

        private static ThemeVariant CurrentThemeVariant =>
            FollowSystemTheme
                ? SystemThemeVariant
                : Current.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
                    ? ThemeVariant.Dark
                    : ThemeVariant.Light;

        /// <summary>
        /// 按用户设置「应该」用的主题变体。
        ///
        /// 供外部做一致性校验：只要 <c>Application.ActualThemeVariant</c> 和它不一致，
        /// 说明有东西把主题改走了（第三方主题、系统事件…），需要重新 ApplyTheme。
        /// </summary>
        public static ThemeVariant DesiredThemeVariant => CurrentThemeVariant;

        /// <summary>一次性应用「明暗主题 + 主题色/渐变 + 背景图 + 字体 + 动效」，启动时调用。</summary>
        public static void ApplyAppearance()
        {
            ApplyTheme();
            ApplyPerformanceMode();
            ApplyTypography();
            ApplyMotion();
            ApplyHighContrast();
            ApplyBackground();
        }

        // ===== 性能模式 =====

        /// <summary>
        /// 应用「性能模式」开关：往应用资源里写一个全局标志，
        /// 样式系统据此关掉过渡动画 / 阴影 / 平滑滚动（见 Styles/Animations.axaml 的 PerformanceMode 相关样式）。
        ///
        /// 这里不动 <see cref="Application.Current"/> 以外的东西：渲染后端、Mica 这些
        /// 必须在窗口 / AppBuilder 层面处理的开关由各自的所有者读取 <see cref="AppSettings.PerformanceMode"/> 决定。
        /// </summary>
        public static void ApplyPerformanceMode()
        {
            if (Application.Current?.Resources is { } resources)
                resources["PerformanceMode"] = Current.PerformanceMode;

            Serilog.Log.Information("性能模式：{State}", Current.PerformanceMode ? "已开启" : "已关闭");
        }

        // ===== 字体 / 字号缩放 =====

        /// <summary>
        /// 字号阶梯的基准值（与 Styles/AppTheme.axaml 里的 FontSize* 一一对应）。
        /// 缩放时按这份基准重算，而不是读当前资源 —— 连续拖动滑块不会累积误差。
        /// </summary>
        private static readonly (string Key, double Base)[] FontScaleTokens =
        {
            ("FontSizeDisplay", 32),
            ("FontSizeHero", 26),
            ("FontSizeTitle", 22),
            ("FontSizeSubtitle", 17),
            ("FontSizeBodyLarge", 15),
            ("FontSizeBody", 14),
            ("FontSizeCaption", 12),
        };

        /// <summary>字体缩放的合法区间，和设置页滑块保持一致。</summary>
        public const double MinFontScale = 0.8;
        public const double MaxFontScale = 1.5;

        /// <summary>
        /// 应用字体与字号缩放。
        ///
        /// <para>
        /// 字号 token 在页面里用 <c>DynamicResource</c> 引用，所以改这里会即时生效；
        /// 各页面不必感知缩放的存在。
        /// </para>
        /// </summary>
        public static void ApplyTypography()
        {
            if (Application.Current?.Resources is not { } resources)
                return;

            var scale = Math.Clamp(Current.FontScale, MinFontScale, MaxFontScale);
            foreach (var (key, baseline) in FontScaleTokens)
                resources[key] = Math.Round(baseline * scale, 1);

            // 字体：空表示跟随系统。Fluent 的控件文字走 ContentControlThemeFontFamily，
            // 页面标题走 AppFont / AppDisplayFont，三者要一起改，否则只有一半变了。
            var family = string.IsNullOrWhiteSpace(Current.FontFamily)
                ? null
                : new FontFamily(Current.FontFamily);

            if (family is null)
            {
                resources.Remove("ContentControlThemeFontFamily");
                resources.Remove("AppFont");
                resources.Remove("AppDisplayFont");
            }
            else
            {
                resources["ContentControlThemeFontFamily"] = family;
                resources["AppFont"] = family;
                resources["AppDisplayFont"] = family;
            }

            Serilog.Log.Information("字体已应用：{Family}，缩放 {Scale:P0}", family?.Name ?? "系统默认", scale);
        }

        // ===== 动效 =====

        /// <summary>动画档位取值：Full / Reduced / Off。</summary>
        public static string AnimationLevel =>
            NormalizeAnimationLevel(Current.AnimationLevel);

        /// <summary>页面切换方式：Slide / Fade / None。</summary>
        public static string PageTransitionMode
        {
            get
            {
                var value = Current.PageTransition;
                return value is "Slide" or "Fade" or "None" ? value : "Slide";
            }
        }

        /// <summary>动画速度倍率（0.5~2），1 为基准；时长要除以它。</summary>
        public static double AnimationSpeed =>
            Math.Clamp(Current.AnimationSpeed <= 0 ? 1 : Current.AnimationSpeed, 0.5, 2.0);

        private static string NormalizeAnimationLevel(string? value) =>
            value is "Full" or "Reduced" or "Off" ? value : "Full";

        /// <summary>
        /// 应用动效设置：往应用资源里写档位与速度，样式系统据此决定动画时长与是否播放。
        ///
        /// <para>
        /// 性能模式是「一键关掉一切」的粗暴开关；动效档位是细分控制。
        /// 两者取更严格的那个 —— 性能模式开着时，档位再怎么设也按 Off 处理。
        /// </para>
        /// </summary>
        public static void ApplyMotion()
        {
            if (Application.Current?.Resources is not { } resources)
                return;

            var level = Current.PerformanceMode ? "Off" : AnimationLevel;

            resources["MotionLevel"] = level;
            resources["AnimationSpeed"] = AnimationSpeed;
            resources["PageTransitionMode"] = PageTransitionMode;

            Serilog.Log.Information("动效设置：档位 {Level}，页面切换 {Transition}，速度 {Speed}x",
                level, PageTransitionMode, AnimationSpeed);
        }

        // ===== 高对比度 =====

        /// <summary>
        /// 高对比度模式：把正文与次要文字拉到纯黑 / 纯白，并加深分割线。
        ///
        /// <para>
        /// 只改文字与描边，不动背景与强调色 —— 强调色是品牌标识，用户选的色不该被覆盖。
        /// </para>
        /// </summary>
        public static void ApplyHighContrast()
        {
            if (Application.Current?.Resources is not { } resources)
                return;

            if (!Current.HighContrast)
            {
                resources.Remove("TextFillColorPrimaryBrush");
                resources.Remove("TextFillColorSecondaryBrush");
                resources.Remove("TextFillColorTertiaryBrush");
                resources.Remove("AppStrokes");
                return;
            }

            var foreground = IsDarkTheme ? Colors.White : Colors.Black;
            var secondary = IsDarkTheme
                ? Color.FromRgb(0xE6, 0xE6, 0xE6)
                : Color.FromRgb(0x1A, 0x1A, 0x1A);

            resources["TextFillColorPrimaryBrush"] = new SolidColorBrush(foreground);
            resources["TextFillColorSecondaryBrush"] = new SolidColorBrush(secondary);
            resources["TextFillColorTertiaryBrush"] = new SolidColorBrush(secondary);

            Serilog.Log.Information("高对比度：已开启");
        }

        // ===== 重置配置 =====

        /// <summary>
        /// 把设置恢复为出厂值。
        ///
        /// <para>
        /// <b>保留</b>账户（凭证）、当前选中的账户、游戏目录、Java 路径与 Java 安装目录 ——
        /// 这些是「用户积累下来的资产」，重置配置的意图是「界面和参数乱了想回到默认」，
        /// 不是「把游戏存档和登录状态一起清掉」。
        /// </para>
        /// </summary>
        public static void ResetToDefaults()
        {
            var keep = new AppSettings
            {
                MicrosoftAccounts = Current.MicrosoftAccounts,
                YggdrasilAccounts = Current.YggdrasilAccounts,
                OfflineAccounts = Current.OfflineAccounts,
                SelectedAccountUuid = Current.SelectedAccountUuid,
                IsMicrosoftAccount = Current.IsMicrosoftAccount,
                MinecraftFolder = Current.MinecraftFolder,
                JavaPath = Current.JavaPath,
                JavaRuntimeRoot = Current.JavaRuntimeRoot,
                IsFirstRun = Current.IsFirstRun,
                PerformanceModeAsked = Current.PerformanceModeAsked,
            };

            Current = keep;
            Save();

            ApplyAll();

            Serilog.Log.Information("设置已重置为默认值（账户、游戏目录与 Java 设置已保留）");
        }

        /// <summary>
        /// 把「当前配置」整体重新刷到界面层（主题、字体、动效、背景、下载）。
        ///
        /// <para>
        /// 重置整份配置、按分区重置、导入备份三处都要做同一串动作，
        /// 抽出来避免以后新增一个 Apply 方法时漏掉某一条路径。
        /// </para>
        /// </summary>
        public static void ApplyAll()
        {
            ApplyTheme();
            ApplyPerformanceMode();
            ApplyTypography();
            ApplyMotion();
            ApplyHighContrast();
            ApplyDownloadSettings();
            ApplyBackground();
        }

        /// <summary>
        /// 每个分区「本区恢复默认」管到哪些字段。
        ///
        /// <para>
        /// 这里刻意是<b>白名单</b>而不是按前缀猜：设置项之间的关联并不按页面分组 ——
        /// 比如性能模式在外观页，但它决定渲染后端，属于「这台机器扛不扛得住」，
        /// 不该被「恢复外观默认」顺手关掉；语言与字体同理，换了就找不着北。
        /// </para>
        ///
        /// <para>
        /// 路径类字段（游戏目录、Java、资源包 / 存档迁移位置、背景图、字体文件）一律不在表内：
        /// 它们是用户攒下来的资产，重置一下配置不该让人重新选一次目录。
        /// </para>
        /// </summary>
        private static readonly Dictionary<string, string[]> SectionFields = new(StringComparer.OrdinalIgnoreCase)
        {
            ["general"] = new[]
            {
                nameof(AppSettings.ShowHomeGreeting), nameof(AppSettings.ShowHomeLaunchCard),
                nameof(AppSettings.ShowHomeRecent), nameof(AppSettings.ShowHomeInstanceOverview),
                nameof(AppSettings.ShowHomeShortcuts), nameof(AppSettings.MinimizeToTray),
                nameof(AppSettings.UpdateIncludePrerelease), nameof(AppSettings.AutoLoginLastAccount),
                nameof(AppSettings.AfterLaunchAction)
            },
            ["game"] = new[]
            {
                nameof(AppSettings.MaxMemoryMb), nameof(AppSettings.MinMemoryMb),
                nameof(AppSettings.MemoryAutoRecommend), nameof(AppSettings.JvmArgs),
                nameof(AppSettings.VersionIsolation), nameof(AppSettings.DisableIpv6),
                nameof(AppSettings.GcMode), nameof(AppSettings.WindowWidth),
                nameof(AppSettings.WindowHeight), nameof(AppSettings.StartFullscreen),
                nameof(AppSettings.UseCustomGameArgs), nameof(AppSettings.GameArgs),
                nameof(AppSettings.AutoBackupSaves), nameof(AppSettings.SaveBackupKeepCount)
            },
            ["appearance"] = new[]
            {
                nameof(AppSettings.Theme),
                nameof(AppSettings.AccentColor),
                nameof(AppSettings.AccentColorEnd), nameof(AppSettings.UseAccentGradient),
                nameof(AppSettings.BackgroundMode), nameof(AppSettings.BackgroundColor),
                nameof(AppSettings.BackgroundImageOpacity), nameof(AppSettings.BackgroundBlur),
                nameof(AppSettings.BackgroundVignette), nameof(AppSettings.BackgroundAnimated),
                nameof(AppSettings.AnimationLevel), nameof(AppSettings.PageTransition),
                nameof(AppSettings.AnimationSpeed), nameof(AppSettings.FontScale),
                nameof(AppSettings.HighContrast)
            },
            ["network"] = new[]
            {
                nameof(AppSettings.DownloadSourceMode), nameof(AppSettings.DownloadSpeedLimitKb),
                nameof(AppSettings.DownloadTimeoutSeconds), nameof(AppSettings.UseBmclMirror),
                nameof(AppSettings.DownloadConcurrency), nameof(AppSettings.DownloadChunkCount),
                nameof(AppSettings.DownloadRetryCount), nameof(AppSettings.EasyTierRelayServer),
                nameof(AppSettings.EasyTierTerracottaCompat),
                nameof(AppSettings.KeepMultiplayerAlive)
            },
            ["advanced"] = new[]
            {
                nameof(AppSettings.LogLevel), nameof(AppSettings.LogRetentionDays),
                nameof(AppSettings.LogToDebugOutput), nameof(AppSettings.CrashReportAutoUpload)
            }
        };

        /// <summary>这个分区有没有可重置的项（「关于」页没有，界面上就不给按钮）。</summary>
        public static bool HasSectionDefaults(string section) => SectionFields.ContainsKey(section);

        /// <summary>
        /// 只把某一个分区管辖的设置恢复默认，返回实际改动了几项。
        ///
        /// <para>
        /// 用来替代「改坏了一个开关却只能整份重置」：整份重置连账户之外的一大堆选择都会丢，
        /// 用户宁可忍着不改。按分区重置的语义是「我只想把这个页面退回刚装好的样子」。
        /// </para>
        /// </summary>
        public static int ResetSection(string section)
        {
            if (!SectionFields.TryGetValue(section, out var names))
                return 0;

            var defaults = new AppSettings();
            var type = typeof(AppSettings);
            var changed = 0;

            foreach (var name in names)
            {
                var property = type.GetProperty(name);
                if (property is null || !property.CanWrite)
                    continue;

                var current = property.GetValue(Current);
                var fallback = property.GetValue(defaults);

                // 逐字段比对：没变的项不算「改动」，否则界面上会显示「已恢复 12 项」而实际一个也没变
                if (Equals(current, fallback))
                    continue;

                property.SetValue(Current, fallback);
                changed++;
            }

            Save();

            if (changed > 0)
            {
                ApplyAll();
                Serilog.Log.Information("设置分区「{Section}」已恢复默认，改动 {Count} 项", section, changed);
            }

            return changed;
        }


        /// <summary>
        /// 刷新主题：先关掉 FluentAvalonia 的「跟随系统」，再写入主题色 / 手写色资源，
        /// **最后**才切明暗主题变体。切换明暗主题、切换主题色 / 渐变都走这里。
        ///
        /// 顺序不能反（1.0.7.14 血案）：
        /// 以前第一步就设 RequestedThemeVariant，而此刻 FluentAvaloniaTheme.PreferSystemTheme
        /// 还是 True —— 它会在自己的变体变更处理里把变体**改回操作系统的主题**。
        /// 结果变体是系统值（比如暗色），而下面第 ③ 步写进去的窗口底色是按应用设置算的（亮色），
        /// 于是出现「浅色底 + 浅色字」：二级窗口整片发白、正文几乎看不见，
        /// 主窗口因为有 Mica 材质垫底才看着正常。
        /// </summary>
        public static void ApplyTheme()
        {
            if (Application.Current is null)
                return;

            // ⓪ 先掐掉 FluentAvalonia 的两个「自作主张」开关：主题与强调色由应用自己管
            if (Application.Current.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault() is { } fa)
            {
                fa.PreferSystemTheme = false;
                fa.PreferUserAccentColor = false;
            }

            // ① 再把主题色写进资源（SystemAccentColor / SystemAccentColorBrush / AppAccentGradientBrush …）
            ApplyAccentCore(ResolvePrimary(), ResolveEnd(), Current.UseAccentGradient, IsDarkTheme);

            // ② 背景图遮罩要随明暗主题换色，否则亮色主题下深色图片会让文字糊成一片
            if (Application.Current.Resources is { } resources)
            {
                resources["AppBackgroundScrimBrush"] = new SolidColorBrush(
                    IsDarkTheme
                        ? Color.FromArgb(0x8C, 0x00, 0x00, 0x00)
                        : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

                // 窗口底色兜底为**不透明纯色**。
                //
                // XAML 里写的是 #26FFFFFF（15% 白 + 透明），它只在两大前提同时成立时才好看：
                //   · 系统支持窗口合成（Windows 11 的 Mica），且它真的启用了；
                //   · 下层还有什么东西可以透出来。
                // 在 Windows 7 这类没有免合成能力的系统上，半透明背景没有可合成的底，
                // 结果就是整窗接近纯白 —— 内容还没画完时看上去就是「白屏」。
                //
                // 所以这里恒提供一份不透明兜底色；Mica 生效时 MainWindow 会把
                // Background 设成 Transparent（局部值优先级高于这里的资源），两者互不干扰。
                resources["AppWindowBackgroundBrush"] = new SolidColorBrush(
                    IsDarkTheme
                        ? Color.FromRgb(0x20, 0x20, 0x20)
                        : Color.FromRgb(0xF3, 0xF3, 0xF3));

                // 二级窗口（登录 / 皮肤管理 / 欢迎向导 / 崩溃分析 …）的整窗底色。
                //
                // ⚠️ 这个键是 WPF 版迁移遗留：Avalonia / FluentAvalonia 都**没有**它，
                // 之前 9 个二级窗口的根 Grid 全是 `{DynamicResource ApplicationBackgroundBrush}`
                // → 解析成 null → 无背景 → 透出窗口默认底色 = **整窗白屏**（用户实测：别的电脑上所有窗口白屏）。
                // 这里跟 AppWindowBackgroundBrush 一起按明暗主题写，保证任何主题下都是不透明色。
                resources["ApplicationBackgroundBrush"] = new SolidColorBrush(
                    IsDarkTheme
                        ? Color.FromRgb(0x20, 0x20, 0x20)
                        : Color.FromRgb(0xF3, 0xF3, 0xF3));

                // 拖入整合包的提示层同样要随主题换色：暗色主题下用深底 + 亮面板，
                // 否则「黑底上盖白面板」会刺眼、也会盖住标题栏。
                resources["AppDropOverlayBrush"] = new SolidColorBrush(
                    IsDarkTheme
                        ? Color.FromArgb(0x99, 0x00, 0x00, 0x00)
                        : Color.FromArgb(0x66, 0x00, 0x00, 0x00));

                resources["AppDropPanelBrush"] = new SolidColorBrush(
                    IsDarkTheme
                        ? Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B)
                        : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
            }

            // ③ 资源全部就位后，最后切主题变体 —— 此时没有任何东西会把它改回去了
            //    「跟随系统」时写 Default，交给平台事件驱动；此时 CurrentThemeVariant
            //    已经解析成系统当前的值，上面算出来的底色不会和变体打架。
            Application.Current.RequestedThemeVariant =
                FollowSystemTheme ? ThemeVariant.Default : CurrentThemeVariant;

            EnsureSystemThemeListener();

            // ④ 记一笔「设置 vs 实际」，以后再有明暗不对的问题能一眼看出是谁没生效
            var actual = Application.Current.ActualThemeVariant;
            if (actual != CurrentThemeVariant)
            {
                Serilog.Log.Warning("主题变体未生效：期望 {Expected}，实际 {Actual}",
                    CurrentThemeVariant, actual);
            }
            else
            {
                Serilog.Log.Information("主题已应用：{Variant}（强调色 {Primary}，渐变 {Gradient}）",
                    actual, ResolvePrimary(), Current.UseAccentGradient);
            }

            _lastAppliedVariant = actual;
        }

        /// <summary>上一次应用时实际生效的主题变体，用于抑制「自己改、自己又收到事件」的回环。</summary>
        private static ThemeVariant? _lastAppliedVariant;

        private static bool _systemThemeHooked;

        /// <summary>
        /// 订阅一次系统明暗变化。
        ///
        /// <para>
        /// 「跟随系统」模式下，变体本身由 Avalonia 自动切；但本工程另有一批
        /// <b>手写进资源的颜色</b>（窗口兜底色、壁纸遮罩、强调色派生色），
        /// 它们不会随变体自动重算，必须在这里补一次。
        /// </para>
        /// </summary>
        private static void EnsureSystemThemeListener()
        {
            if (_systemThemeHooked || Application.Current is not { } app)
                return;

            _systemThemeHooked = true;
            app.ActualThemeVariantChanged += (_, _) =>
            {
                if (!FollowSystemTheme)
                    return;

                var now = app.ActualThemeVariant;
                if (_lastAppliedVariant == now)
                    return;

                ApplyTheme();
            };
        }

        /// <summary>主题色 / 渐变发生变化时的统一入口。</summary>
        public static void ApplyAccentChange() => ApplyTheme();

        /// <summary>
        /// 按当前配置写入强调色资源键（含可选渐变）。
        /// </summary>
        public static void ApplyAccent()
        {
            ApplyAccentCore(ResolvePrimary(), ResolveEnd(), Current.UseAccentGradient, IsDarkTheme);
        }

        /// <summary>主色。只来自用户在「设置 → 外观」里挑的颜色。</summary>
        private static Color ResolvePrimary() =>
            TryParseColor(Current.AccentColor, out var parsed) ? parsed : DefaultAccentColor;

        private static Color ResolveEnd()
        {
            // 新设计固定纯色，终点色与主色一致（保留方法签名给历史调用点）
            return ResolvePrimary();
        }

        /// <summary>
        /// 覆盖 FluentAvalonia 的强调色资源，并把 <c>AppAccentGradientBrush</c> 换成当前的主色渐变。
        /// </summary>
        public static void ApplyAccentCore(Color primary, Color end, bool gradient, bool isDark)
        {
            if (Application.Current?.Resources is not { } resources)
                return;

            var useGradient = gradient && primary != end;

            // 先交给 FluentAvalonia 官方入口重算强调色：它内部会按主色派生出一整套资源
            // （AccentDark1~3 / AccentLight1~3 / NavigationView 系列 / 文本与描边派生色 …）。
            // 只在 Application.Resources 上硬写几个键覆盖不全，这就是「改了主题色但很多地方不变」的根因。
            if (Application.Current.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault() is { } fa)
            {
                fa.PreferSystemTheme = false;

                // 为 true 时 FluentAvalonia 会把**操作系统**强调色写进 NavigationView 等专属资源，
                // 覆盖掉应用内选的主题色，必须关掉。
                fa.PreferUserAccentColor = false;

                fa.CustomAccentColor = primary;
            }

            resources["SystemAccentColor"] = primary;
            resources["SystemAccentColorPrimary"] = primary;
            resources["SystemAccentColorSecondary"] = useGradient ? end : primary;
            resources["SystemAccentColorTertiary"] = useGradient ? Blend(primary, end, 0.5) : primary;
            resources["SystemAccentColorBrush"] = new SolidColorBrush(primary);

            // FluentAvalonia 的按钮等控件经由 AccentFillColorDefault 取色，
            // 只改 *Brush 版本它们不会跟着变，所以 Color 和 Brush 都要写。
            resources["AccentFillColorDefault"] = primary;
            resources["AccentFillColorSecondary"] = primary;
            resources["AccentFillColorTertiary"] = primary;

            resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(primary);
            resources["AccentFillColorSecondaryBrush"] = new SolidColorBrush(primary) { Opacity = 0.7 };
            resources["AccentFillColorTertiaryBrush"] = new SolidColorBrush(primary) { Opacity = 0.5 };
            resources["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(Colors.White);
            resources["AccentTextFillColorSecondaryBrush"] = new SolidColorBrush(Colors.White) { Opacity = 0.7 };

            // 主操作底色。
            //
            // 新语言里它是**纯色**，不再走蓝→青的渐变：渐变按钮属于装饰，
            // 在明亮清爽的界面里会把注意力从内容抢到按钮上。
            // 键名沿用 AppAccentGradientBrush，是为了不让历史页面集体失配。
            // 同时补一支加深色，供按钮 hover / pressed 用 —— 只靠透明度做反馈会连文字一起变淡。
            resources["AppAccentGradientBrush"] = new SolidColorBrush(primary);
            resources["AppAccentDeepBrush"] = new SolidColorBrush(Darken(primary, 0.16));

            // 强调色浅底：图标块、计数徽标、选中卡片的底板。
            // 用固定透明度的主色而不是硬编码 #1F0078D4 —— 换主题色时这些浅底要跟着变。
            resources["AppAccentSurfaceBrush"] = new SolidColorBrush(primary) { Opacity = 0.12 };
            resources["AppAccentSurfaceStrongBrush"] = new SolidColorBrush(primary) { Opacity = 0.20 };

            // 强调色光晕（径向衰减到全透明）：只给启动页的主按钮做"呼吸"用。
            // 渐变停靠点必须是 Color 而不是 Brush —— GradientStop.Color 是 Color 类型，
            // 顺手把 alpha 按 55% / 0% 写死，页面上不需要再叠透明度。
            resources["AppAccentHaloBrush"] = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(WithAlpha(primary, 0.55), 0),
                    new GradientStop(WithAlpha(primary, 0), 1)
                }
            };

            // 强调色细线：页标题下那道 2px×28 的短横、导航选中条共用。
            resources["AppAccentHairlineBrush"] = new SolidColorBrush(primary) { Opacity = 0.45 };

            // 背景极光的主光斑：主色向外淡出。透明度比光晕更低，只负责"有颜色"。
            resources["AppAccentAuroraBrush"] = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(WithAlpha(primary, isDark ? 0.30 : 0.22), 0),
                    new GradientStop(WithAlpha(primary, 0), 1)
                }
            };

            // 侧边栏（FANavigationView）的选中态用的是 NavigationView 专属资源键。
            // CustomAccentColor 已经会重算它们，这里再显式写一遍作为兜底（版本差异 / 未覆盖到的键）。
            resources["NavigationViewSelectionIndicatorForeground"] = new SolidColorBrush(primary);
            resources["NavigationViewItemBackgroundSelected"] = new SolidColorBrush(primary) { Opacity = 0.14 };
            resources["NavigationViewItemBackgroundSelectedPointerOver"] = new SolidColorBrush(primary) { Opacity = 0.20 };
            resources["NavigationViewItemBackgroundSelectedPressed"] = new SolidColorBrush(primary) { Opacity = 0.26 };
            resources["NavigationViewItemBorderBrushSelected"] = new SolidColorBrush(primary);

            // 选中项的图标 / 文字：直接取主色在暗色主题下可能撞上深色背景而看不清，
            // 因此按主题明暗把主色向可读方向提亮 / 压暗后再用。
            var selectedForeground = isDark ? Lighten(primary, 0.35) : Darken(primary, 0.18);
            resources["NavigationViewItemForegroundSelected"] = new SolidColorBrush(selectedForeground);
            resources["NavigationViewItemForegroundSelectedPointerOver"] = new SolidColorBrush(selectedForeground);
            resources["NavigationViewItemForegroundSelectedPressed"] = new SolidColorBrush(Darken(selectedForeground, 0.12));
        }

        /// <summary>
        /// 把当前背景配置广播给主窗口（主窗口负责实际的图片加载、模糊与暗角渲染）。
        /// </summary>
        public static void ApplyBackground()
        {
            var mode = Current.BackgroundMode;
            if (mode is not ("None" or "Color" or "Image"))
                mode = "Image";

            ApplyContentBackground(mode);

            WeakReferenceMessenger.Default.Send(new BackgroundChangedMessage(
                ImagePath: string.IsNullOrWhiteSpace(Current.BackgroundImagePath) ? null : Current.BackgroundImagePath,
                Opacity: Math.Clamp(Current.BackgroundImageOpacity, 0.05, 1),
                Mode: mode,
                ColorHex: Current.BackgroundColor,
                Blur: Math.Clamp(Current.BackgroundBlur, 0, 1),
                Vignette: Math.Clamp(Current.BackgroundVignette, 0, 1),
                Animated: Current.BackgroundAnimated));
        }

        /// <summary>
        /// 让导航内容区透出窗口背景。只有在确实「有背景」时才把它改成透明。
        /// </summary>
        private static void ApplyContentBackground(string mode)
        {
            var resources = Application.Current?.Resources;
            if (resources == null)
                return;

            var hasBackground = mode switch
            {
                "Color" => true,
                "Image" => !string.IsNullOrWhiteSpace(Current.BackgroundImagePath),
                _ => false
            };

            if (hasBackground)
                resources["NavigationViewContentBackground"] = new SolidColorBrush(Colors.Transparent);
            else
                resources.Remove("NavigationViewContentBackground");
        }

        private static IBrush BuildAccentBrush(Color primary, Color end, bool gradient)
        {
            if (!gradient)
                return new SolidColorBrush(primary);

            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(primary, 0),
                    new GradientStop(end, 1)
                }
            };
        }

        /// <summary>
        /// 给颜色套一个透明度（0~1）。用于生成光晕渐变的两个停靠点。
        /// </summary>
        private static Color WithAlpha(Color color, double alpha) =>
            Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0d, 1d) * 255), color.R, color.G, color.B);

        private static Color Blend(Color from, Color to, double ratio)
        {
            byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * ratio);

            return Color.FromArgb(
                Mix(from.A, to.A),
                Mix(from.R, to.R),
                Mix(from.G, to.G),
                Mix(from.B, to.B));
        }

        /// <summary>向白色靠拢 amount（0~1），用于暗色主题下保证文字对比度。</summary>
        private static Color Lighten(Color color, double amount) => Blend(color, Colors.White, amount);

        /// <summary>向黑色靠拢 amount（0~1），用于亮色主题下压暗过亮的主色。</summary>
        private static Color Darken(Color color, double amount) => Blend(color, Colors.Black, amount);

        public static bool TryParseColor(string value, out Color color)
        {
            color = Colors.Transparent;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var text = value.StartsWith("#") ? value : "#" + value;
            return Color.TryParse(text, out color);
        }

        public static string ColorToString(Color color) =>
            $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
