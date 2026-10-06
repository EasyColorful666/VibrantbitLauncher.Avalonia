using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
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
        public string AccentColor { get; set; } = "#FF0078D4";
        /// <summary>主题渐变色的终点色（UseAccentGradient 为 true 时生效）。</summary>
        public string AccentColorEnd { get; set; } = "#FF00B7C3";
        /// <summary>是否启用「主题渐变色」。关闭时全程使用 AccentColor 单色。</summary>
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

        // ===== 网络 · 下载 =====
        /// <summary>使用 BMCLAPI 镜像下载（国内加速）。</summary>
        public bool UseBmclMirror { get; set; }
        /// <summary>游戏文件下载并发数。</summary>
        public int DownloadThreads { get; set; } = 10;
        /// <summary>下载失败重试次数。</summary>
        public int DownloadRetryCount { get; set; } = 4;

        // ===== 联机（EasyTier） =====
        /// <summary>EasyTier 中继节点地址，默认用社区免费共享节点。</summary>
        public string EasyTierRelayServer { get; set; } = "tcp://easytier.weiai.org.cn:11010";
        /// <summary>上次房主侧使用的联机密钥（同时作为网络名与网络密码）。</summary>
        public string EasyTierNetworkKey { get; set; } = string.Empty;

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
        /// 把下载相关设置应用到 MinecraftLaunch。
        ///
        /// 实测 <c>InitializeHelper.Initialize</c> 内部只是把参数写进 <c>DownloadManager</c> 的静态属性，
        /// 可反复调用且立即生效（镜像开关会立刻影响 <c>DownloadManager.BmclApi.TryFindUrl</c> 的改写结果），
        /// 所以设置页改完直接调一次即可，无需重启。
        ///
        /// 注意 BMCLAPI 的改写范围：launchermeta / piston-meta / libraries / resources / launcher.mojang.com
        /// 会被换成 bmclapi2.bangbang93.com，但 <c>piston-data.mojang.com</c> 不在其覆盖内
        /// （部分版本的 client.jar 仍走官方源）。
        /// </summary>
        public static void ApplyDownloadSettings()
        {
            try
            {
                MinecraftLaunch.InitializeHelper.Initialize(settings =>
                {
                    settings.MaxThread = 256;
                    settings.MaxFragment = 128;
                    settings.MaxRetryCount = Math.Clamp(Current.DownloadRetryCount, 0, 10);
                    settings.IsEnableMirror = Current.UseBmclMirror;
                    settings.IsEnableFragment = false;
                });

                Serilog.Log.Information("下载设置：下载源={Source} 重试={Retry} 次",
                    Current.UseBmclMirror ? "BMCLAPI 镜像" : "官方源",
                    Current.DownloadRetryCount);
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
            Current.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);

        private static ThemeVariant CurrentThemeVariant =>
            IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

        /// <summary>一次性应用「明暗主题 + 主题色/渐变 + 背景图」，启动时调用。</summary>
        public static void ApplyAppearance()
        {
            ApplyTheme();
            ApplyBackground();
        }

        /// <summary>
        /// 刷新主题：先写入当前主题色 / 渐变，再切换明暗主题，最后按明暗主题重算背景遮罩。
        /// 切换明暗主题、切换主题色 / 渐变都走这里。
        /// </summary>
        public static void ApplyTheme()
        {
            if (Application.Current is null)
                return;

            // ① 先切明暗主题
            Application.Current.RequestedThemeVariant = CurrentThemeVariant;

            // ② 再把主题色写进资源（SystemAccentColor / SystemAccentColorBrush / AppAccentGradientBrush …）
            ApplyAccentCore(ResolvePrimary(), ResolveEnd(), Current.UseAccentGradient, IsDarkTheme);

            // ③ 背景图遮罩要随明暗主题换色，否则亮色主题下深色图片会让文字糊成一片
            if (Application.Current.Resources is { } resources)
            {
                resources["AppBackgroundScrimBrush"] = new SolidColorBrush(
                    IsDarkTheme
                        ? Color.FromArgb(0x8C, 0x00, 0x00, 0x00)
                        : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            }
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

        private static Color ResolvePrimary() =>
            TryParseColor(Current.AccentColor, out var parsed) ? parsed : DefaultAccentColor;

        private static Color ResolveEnd() =>
            TryParseColor(Current.AccentColorEnd, out var parsed) ? parsed : ResolvePrimary();

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

            // 页面里的渐变强调条、渐变预览统一读这个键
            resources["AppAccentGradientBrush"] = BuildAccentBrush(primary, end, useGradient);

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

        /// <summary>把当前配置的背景图广播给主窗口（主窗口负责实际的图片加载与渲染）。</summary>
        public static void ApplyBackground()
        {
            ApplyContentBackground();

            WeakReferenceMessenger.Default.Send(
                new BackgroundChangedMessage(Current.BackgroundImagePath, Current.BackgroundImageOpacity));
        }

        /// <summary>
        /// 让导航内容区透出窗口背景。只有在确实设置了壁纸时才把它改成透明。
        /// </summary>
        private static void ApplyContentBackground()
        {
            var resources = Application.Current?.Resources;
            if (resources == null)
                return;

            if (!string.IsNullOrWhiteSpace(Current.BackgroundImagePath))
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
