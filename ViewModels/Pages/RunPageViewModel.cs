using VibrantbitLauncher.Helpers;
using CommunityToolkit.Mvvm.Messaging;
using MinecraftLaunch.Base.Models.Authentication;
using MinecraftLaunch.Base.Models.Game;
using MinecraftLaunch.Components.Authenticator;
using MinecraftLaunch.Components.Logging;
using MinecraftLaunch.Extensions;
using MinecraftLaunch.Utilities;
using VibrantbitLauncher.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using VibrantbitLauncher.ViewModels.Windows;
using VibrantbitLauncher.Views.Pages;
using VibrantbitLauncher.Views.Windows;
using System.Threading.Tasks;
namespace VibrantbitLauncher.ViewModels.Pages
{
    public partial class RunPageViewModel : ObservableObject
    {
        // Java 列表由 LoadAsync() 在后台线程填充。
        // 原实现在字段初始化器里同步枚举系统 Java（JavaUtil.EnumerableJavaAsync().ToBlockingEnumerable()），
        // 会在候选路径为空时抛 ArgumentException，并把 UI 线程卡住；构造函数里不能做同步 IO。
        private List<JavaEntry> asyncJavas = new();
        private ObservableCollection<MinecraftEntry> minecrafts = new ObservableCollection<MinecraftEntry>();
        private ObservableCollection<LocalVersion> minecraftVersions = new ObservableCollection<LocalVersion>();
        private ObservableCollection<string> javaVersions = new ObservableCollection<string>();
        private MinecraftRunner runner;
        private readonly INotificationService _notification = AppServices.Notifications!;
        string selectedVersion = "";
        private string accountName;

        public bool IsLoaded { get; set; }
        public RelayCommand LoadCommand { get; set; }
        public RelayCommand RefreshCommand { get; set; }

        public ObservableCollection<LocalVersion> MinecraftVersions
        {
            get => minecraftVersions;
            set => SetProperty(ref minecraftVersions, value);
        }
        public string SelectedVersion
        {
            get => selectedVersion;
            set => SetProperty(ref selectedVersion, value);
        }
        public string AccountName
        {
            get => accountName;
            set => SetProperty(ref accountName, value);
        }

        public RunPageViewModel()
        {
            IsLoaded = false;
            LoadCommand = new RelayCommand(Load);
            RefreshCommand = new RelayCommand(Refresh);
        }

        private static List<string> GetLocalVersionIds()
        {
            var result = new List<string>();
            var mcFolder = MainWindowViewModel.MainModel.MinecraftFolder ?? "./.minecraft";
            var versionsPath = Path.Combine(mcFolder, "versions");
            if (!Directory.Exists(versionsPath)) return result;

            var allDirs = Directory.GetDirectories(versionsPath);
            var inheritedBaseIds = new HashSet<string>();

            // 第一遍：读取所有版本 JSON，收集被其他版本继承的基础版本 ID
            foreach (var dir in allDirs)
            {
                var dirName = Path.GetFileName(dir);
                var jsonPath = Path.Combine(dir, dirName + ".json");
                if (!File.Exists(jsonPath)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
                    if (doc.RootElement.TryGetProperty("inheritsFrom", out var inheritsEl))
                    {
                        var baseId = inheritsEl.GetString();
                        if (!string.IsNullOrEmpty(baseId))
                            inheritedBaseIds.Add(baseId);
                    }
                }
                catch { }
            }

            // 第二遍：只列出不是"被继承基础版本"的版本
            foreach (var dir in allDirs)
            {
                var dirName = Path.GetFileName(dir);
                var jsonPath = Path.Combine(dir, dirName + ".json");
                if (!File.Exists(jsonPath)) continue;
                if (inheritedBaseIds.Contains(dirName)) continue;
                result.Add(dirName);
            }
            return result;
        }

        public async Task LoadAsync()
        {
            await Task.Run(async () =>
            {
                try
                {
                    var javas = await JavaHelper.FindJavasAsync();
                    asyncJavas = javas;

                    // 将设置中的自定义 Java 也加入列表（同样要过静态体检，
                    // 避免把 PATH 里的 Oracle 转发器当成可用 Java 存进列表）
                    var customJava = MainWindowViewModel.MainModel.JavaPath;
                    if (JavaHelper.IsUsableJava(customJava)
                        && !asyncJavas.Any(j => string.Equals(j.JavaPath, customJava, StringComparison.OrdinalIgnoreCase)))
                    {
                        asyncJavas.Add(new JavaEntry { JavaPath = customJava, Is64bit = true });
                    }
                }
                catch { }

                try
                {
                    var versionIds = GetLocalVersionIds();
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        minecraftVersions.Clear();
                        foreach (var id in versionIds)
                        {
                            minecraftVersions.Add(new LocalVersion { Version = id, RunCommand = new(RunMinecraft), SettingsCommand = new(Settings) });
                        }
                    });

                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        javaVersions.Clear();
                        foreach (var j in asyncJavas)
                        {
                            javaVersions.Add(j.JavaVersion);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() => _notification.Error($"无法获取本地版本: {ex.Message}"));
                }
            });

            IsLoaded = true;
        }

        private int refreshing;

        private void Refresh()
        {
            // 反复点导航会让 Loaded 重复触发。已有刷新在跑就直接忽略，
            // 否则会累积多个扫盘任务并反复重建列表，表现为"点来点去越来越卡"。
            if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0)
                return;

            Task.Run(async () =>
            {
                try
                {
                    var versionIds = GetLocalVersionIds();
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        minecraftVersions.Clear();
                        foreach (var id in versionIds)
                        {
                            minecraftVersions.Add(new LocalVersion { Version = id, RunCommand = new(RunMinecraft), SettingsCommand = new(Settings) });
                        }
                    });
                }
                catch (Exception ex)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() => _notification.Error($"无法刷新本地版本: {ex.Message}"));
                }
                finally
                {
                    Interlocked.Exchange(ref refreshing, 0);
                }
            });
        }

        private void Load()
        {
            Refresh();
        }

        void Settings(string McVersion)
        {
            if (!string.IsNullOrEmpty(McVersion))
            {
                WeakReferenceMessenger.Default.Send<string, string>(McVersion, "McVersionForManage");
                WeakReferenceMessenger.Default.Send<Type, string>(typeof(VersionManagePage), "NavigateTo");
            }
            else
            {
                _notification.Error("请选择一个版本");
            }
        }

        private static readonly HashSet<string> launchingVersions = new();

        async void RunMinecraft(string McVersion)
        {
            if (string.IsNullOrEmpty(McVersion))
            {
                ShowError("请选择一个版本");
                return;
            }

            lock (launchingVersions)
            {
                if (launchingVersions.Contains(McVersion))
                {
                    ShowInfo($"{McVersion} 正在启动中，请稍候...");
                    return;
                }
                launchingVersions.Add(McVersion);
            }

            // 启动过程的逐步骤诊断。
            // 以前这里自己往 launch_debug.log 追加（另一份「日志系统」，散在程序目录里没人清理），
            // 现在并入统一日志：等级是 Debug，所以默认的「信息」级别下不会污染日志，
            // 需要排查「点了启动没反应」时，到设置里把等级调成「调试」再复现即可。
            static void Diag(string msg) => Serilog.Log.Debug("[启动] {Message}", msg);

            try
            {
                Diag($"=== Launch start: {McVersion} ===");

                // 用设置中的游戏目录初始化 parser
                var mcFolder = SettingsService.ResolveMinecraftFolder();
                Diag($"mcFolder: {mcFolder}");
                var parser = new MinecraftParser(mcFolder);

                MinecraftEntry selectedMinecraftEntry;
                try
                {
                    selectedMinecraftEntry = parser.GetMinecraft(McVersion);
                    Diag($"Parsed: {selectedMinecraftEntry?.Id}, Type: {selectedMinecraftEntry?.GetType().Name}");
                }
                catch (Exception ex)
                {
                    Diag($"Parse FAILED: {ex.GetType().Name}: {ex.Message}");
                    ShowError($"解析版本失败: {ex.Message}");
                    lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                    return;
                }

                if (selectedMinecraftEntry == null)
                {
                    Diag("Entry is null");
                    ShowError($"未找到版本: {McVersion}");
                    lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                    return;
                }

                // 匹配 Java
                JavaEntry javaPath = null;
                try
                {
                    javaPath = selectedMinecraftEntry.GetAppropriateJava(asyncJavas);
                    Diag($"Java matched: {javaPath?.JavaPath} (version {javaPath?.JavaVersion})");
                }
                catch (Exception ex)
                {
                    Diag($"GetAppropriateJava threw: {ex.Message}");
                    var customJavaPath = MainWindowViewModel.MainModel.JavaPath;
                    if (JavaHelper.IsUsableJava(customJavaPath))
                    {
                        javaPath = new JavaEntry { JavaPath = customJavaPath, Is64bit = true };
                        Diag($"Using custom Java: {customJavaPath}");
                    }
                    else if (asyncJavas.Count > 0)
                    {
                        javaPath = asyncJavas.First();
                        Diag($"Fallback to first Java: {javaPath?.JavaPath}");
                    }
                    else
                    {
                        Diag("No Java found");
                        ShowError("未找到匹配的 Java 版本，请在设置中选择 Java");
                        lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                        return;
                    }
                }

                // 启动前再体检一次选中的 Java：PATH 里的 Oracle 转发器（javapath / java8path）
                // 会让 java.exe 弹出「could not find java.dll」的模态框，必须换成真正能用的那一份。
                if (!JavaHelper.IsUsableJava(javaPath?.JavaPath))
                {
                    Diag($"Java not usable: {javaPath?.JavaPath}");
                    var fallback = asyncJavas.FirstOrDefault(j => JavaHelper.IsUsableJava(j.JavaPath));
                    if (fallback == null)
                    {
                        ShowError($"Java 不可用: {javaPath?.JavaPath}（该目录下没有 java.dll，请在设置里重新选择 Java）");
                        lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                        return;
                    }

                    Diag($"Fallback to usable Java: {fallback.JavaPath}");
                    javaPath = fallback;
                }

                // 检查账户
                if (MainWindowViewModel.MainModel.Account == null)
                {
                    Diag("Account is null");
                    ShowError("请先登录一个账户");
                    lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                    return;
                }
                Diag($"Account: {MainWindowViewModel.MainModel.Account.Name}");

                // 微软账户：启动前刷新 access token
                if (MainWindowViewModel.MainModel.IsMicrosoftAccount && MainWindowViewModel.MainModel.Account is MicrosoftAccount msAccount)
                {
                    try
                    {
                        Diag("Refreshing Microsoft account token...");
                        var authenticator = new MicrosoftAuthenticator("f4c1c237-e68f-4866-a998-82f0db041c55");
                        var newProfile = await authenticator.RefreshAsync(msAccount);
                        MainWindowViewModel.MainModel.Account = newProfile;
                        SettingsService.SaveMicrosoftAccount(newProfile);
                        Diag($"Token refreshed successfully, new account: {newProfile.Name}");
                    }
                    catch (Exception ex)
                    {
                        Diag($"Token refresh failed: {ex.GetType().Name}: {ex.Message}");
                        ShowError($"令牌刷新失败，请重新登录微软账户: {ex.Message}");
                        lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                        return;
                    }
                }

                Diag("Launching manually (bypassing MinecraftLaunch runner)...");
                ShowInfo($"正在启动 {McVersion}，请稍等...");

                // 读版本 JSON、拼 classpath、逐个检查几百个库文件是否存在 —— 全是同步文件 IO。
                // 丢到线程池执行，否则点「运行」的那一瞬间 UI 会僵住。
                var rawProcess = await Task.Run(() => LaunchMinecraftManually(mcFolder, McVersion, javaPath, MainWindowViewModel.MainModel.Account, MainWindowViewModel.MainModel.IsMicrosoftAccount, Diag));
                Diag($"Manual launch returned, process null? {rawProcess == null}");

                if (rawProcess == null)
                {
                    ShowError("启动失败：进程未创建");
                    lock (launchingVersions) { launchingVersions.Remove(McVersion); }
                    return;
                }

                try { Diag($"Process Id: {rawProcess.Id}, HasExited: {rawProcess.HasExited}, StartTime: {rawProcess.StartTime}"); } catch (Exception ex) { Diag($"Process info error: {ex.Message}"); }

                // 捕获输出
                rawProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) { Diag($"[STDOUT] {e.Data}"); writeLog(DateTime.Now.ToString("HH:mm:ss"), e.Data); } };
                rawProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) { Diag($"[STDERR] {e.Data}"); writeLog(DateTime.Now.ToString("HH:mm:ss"), "[ERR] " + e.Data); } };
                rawProcess.BeginOutputReadLine();
                rawProcess.BeginErrorReadLine();

                // 等待 3 秒后再次检查进程状态
                await Task.Delay(3000);
                try
                {
                    bool exited = rawProcess.HasExited;
                    Diag($"After 3s - HasExited: {exited}");
                    if (exited)
                    {
                        Diag($"After 3s - ExitCode: {rawProcess.ExitCode}");
                    }
                    else
                    {
                        Diag($"After 3s - Process still running, Responding: {rawProcess.Responding}, MainWindowTitle: '{rawProcess.MainWindowTitle}'");
                    }
                }
                catch (Exception ex) { Diag($"After 3s check error: {ex.Message}"); }

                void HandleExit()
                {
                    int exitCode = -1;
                    try { exitCode = rawProcess.ExitCode; } catch { }
                    Diag($"Process exited with code {exitCode}");

                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        if (exitCode != 0)
                        {
                            ShowError($"Minecraft 异常退出（代码 {exitCode}）");
                            try
                            {
                                var window = new CrashAnalysisWindow(selectedMinecraftEntry);
                                _ = AppLifetime.ShowDialogAsync(window);
                            }
                            catch (Exception ex)
                            {
                                Diag($"CrashAnalysisWindow failed: {ex.Message}");
                            }
                        }
                        else
                        {
                            ShowInfo("Minecraft 已正常退出");
                        }
                    });
                }

                rawProcess.EnableRaisingEvents = true;
                rawProcess.Exited += (_, _) => HandleExit();

                // 防竞态：如果进程已经秒退，直接处理
                bool alreadyExited = false;
                try { alreadyExited = rawProcess.HasExited; } catch { }
                if (alreadyExited)
                {
                    Diag("Process already exited before event attached");
                    HandleExit();
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() => ShowSuccess("Minecraft 已启动"));

                    // 确认游戏真的起来了（不是秒退）再执行「启动后行为」，
                    // 否则会把用户的启动器一起关掉而游戏根本没开起来。
                    ApplyAfterLaunchAction();
                }

                Diag("=== Launch setup complete, waiting for process ===");
                lock (launchingVersions) { launchingVersions.Remove(McVersion); }
            }
            catch (InvalidOperationException ex)
            {
                Diag($"InvalidOperationException: {ex.Message}");
                ShowError("启动失败: 请登录一个用户");
                lock (launchingVersions) { launchingVersions.Remove(McVersion); }
            }
            catch (Exception ex)
            {
                Diag($"Unhandled: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                ShowError($"启动失败: {ex.Message}");
                lock (launchingVersions) { launchingVersions.Remove(McVersion); }
            }
        }

        private static Process LaunchMinecraftManually(string mcFolder, string versionId, JavaEntry java, Account account, bool forceMicrosoft, Action<string> diag)
        {
            // 转成绝对路径，避免 WorkingDirectory 与 classpath 路径不一致
            mcFolder = Path.GetFullPath(mcFolder);
            diag($"Absolute mcFolder: {mcFolder}");

            var versionJsonPath = Path.Combine(mcFolder, "versions", versionId, versionId + ".json");
            diag($"Reading version JSON: {versionJsonPath}");
            var versionJson = JsonDocument.Parse(File.ReadAllText(versionJsonPath)).RootElement;

            var libraries = new List<string>();
            string mainClass = "";
            var jvmArgs = new List<string>();
            var gameArgs = new List<string>();
            string assetsIndex = "latest";
            string parentId = null;

            // 处理父版本
            if (versionJson.TryGetProperty("inheritsFrom", out var inh))
            {
                parentId = inh.GetString();
                diag($"Inherits from: {parentId}");
                var parentPath = Path.Combine(mcFolder, "versions", parentId, parentId + ".json");
                if (File.Exists(parentPath))
                {
                    var parentJson = JsonDocument.Parse(File.ReadAllText(parentPath)).RootElement;
                    if (parentJson.TryGetProperty("libraries", out var pl))
                        foreach (var lib in pl.EnumerateArray())
                        {
                            var lp = GetLibraryPath(lib, mcFolder);
                            if (lp != null) libraries.Add(lp);
                        }
                    if (parentJson.TryGetProperty("mainClass", out var pm))
                        mainClass = pm.GetString();
                    if (parentJson.TryGetProperty("arguments", out var pa))
                    {
                        if (pa.TryGetProperty("jvm", out var pj))
                            foreach (var a in pj.EnumerateArray())
                                if (a.ValueKind == JsonValueKind.String) jvmArgs.Add(a.GetString());
                        if (pa.TryGetProperty("game", out var pg))
                            foreach (var a in pg.EnumerateArray())
                                if (a.ValueKind == JsonValueKind.String) gameArgs.Add(a.GetString());
                    }
                    if (parentJson.TryGetProperty("assets", out var pas))
                        assetsIndex = pas.GetString();
                }
            }

            // 处理子版本（覆盖）
            if (versionJson.TryGetProperty("libraries", out var cl))
                foreach (var lib in cl.EnumerateArray())
                {
                    var lp = GetLibraryPath(lib, mcFolder);
                    if (lp != null) libraries.Add(lp);
                }
            if (versionJson.TryGetProperty("mainClass", out var cm))
                mainClass = cm.GetString();
            if (versionJson.TryGetProperty("arguments", out var ca))
            {
                // 子版本的参数追加到父版本后面（不替换）
                if (ca.TryGetProperty("jvm", out var cj))
                    foreach (var a in cj.EnumerateArray())
                        if (a.ValueKind == JsonValueKind.String) jvmArgs.Add(a.GetString());
                if (ca.TryGetProperty("game", out var cg))
                    foreach (var a in cg.EnumerateArray())
                        if (a.ValueKind == JsonValueKind.String) gameArgs.Add(a.GetString());
            }
            if (versionJson.TryGetProperty("assets", out var cas))
                assetsIndex = cas.GetString();

            // client.jar
            var clientJar = Path.Combine(mcFolder, "versions", versionId, versionId + ".jar");
            if (!File.Exists(clientJar) && parentId != null)
                clientJar = Path.Combine(mcFolder, "versions", parentId, parentId + ".jar");
            if (File.Exists(clientJar))
                libraries.Add(clientJar);
            else
                diag($"WARNING: client.jar not found at {clientJar}");

            // 检查缺失的库
            var missing = libraries.Where(l => !File.Exists(l)).ToList();
            if (missing.Count > 0)
                diag($"WARNING: {missing.Count} missing libraries: {string.Join(", ", missing.Take(5))}");
            libraries = libraries.Where(l => File.Exists(l)).ToList();

            var classpath = string.Join(";", libraries);
            diag($"Libraries: {libraries.Count}, MainClass: {mainClass}, Assets: {assetsIndex}");
            diag($"JVM args: {string.Join(" ", jvmArgs)}");
            diag($"Game args count: {gameArgs.Count}");
            // 确认 fabric-loader 在 classpath 里
            var fabricLoaderInCp = libraries.Any(l => l.Contains("fabric-loader"));
            diag($"fabric-loader in classpath: {fabricLoaderInCp}");
            if (fabricLoaderInCp)
            {
                var fl = libraries.First(l => l.Contains("fabric-loader"));
                diag($"fabric-loader path: {fl}, exists: {File.Exists(fl)}, size: {(File.Exists(fl) ? new FileInfo(fl).Length : 0)}");
            }

            // 提取账户属性
            string accName = account?.Name ?? "Player";
            string accUuid = account?.Uuid.ToString() ?? "";
            string accToken = account?.AccessToken ?? "";
            bool isMicrosoft = forceMicrosoft || account is MicrosoftAccount;
            string accType = isMicrosoft ? "msa" : "legacy";
            diag($"Account: {accName}, type: {account?.GetType().Name}, isMicrosoft: {isMicrosoft}, userType: {accType}");

            var psi = new ProcessStartInfo
            {
                FileName = java.JavaPath,
                WorkingDirectory = mcFolder,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var nativesDir = Path.Combine(mcFolder, "versions", versionId, "natives");
            Directory.CreateDirectory(nativesDir);

            // 启动配置（设置页「启动」分类）：
            // 全部先夹紧再使用 —— 用户可能手改 settings.json 写出 0 / 负数，
            // -Xmx0m 之类会让 JVM 直接起不来，而报错信息完全看不出是配置问题。
            var cfg = SettingsService.Current;
            var maxMb = Math.Clamp(cfg.MaxMemoryMb, 512, 65536);
            var minMb = Math.Clamp(cfg.MinMemoryMb, 128, Math.Max(128, maxMb));

            // 版本隔离：开启时每个版本使用独立的游戏目录（saves / mods / config 等）
            var gameDir = cfg.VersionIsolation
                ? Path.Combine(mcFolder, "versions", versionId)
                : mcFolder;
            Directory.CreateDirectory(gameDir);
            diag($"Version isolation: {cfg.VersionIsolation}, gameDir: {gameDir}");

            psi.ArgumentList.Add($"-Xmx{maxMb}m");
            psi.ArgumentList.Add($"-Xms{minMb}m");
            diag($"Memory: -Xmx{maxMb}m -Xms{minMb}m, GC={cfg.GcMode}, IPv4Only={cfg.DisableIpv6}");

            // 垃圾回收器：不指定时交给 JVM 自己决定（新版 JVM 默认就是 G1）
            switch (cfg.GcMode)
            {
                case "G1GC":
                    psi.ArgumentList.Add("-XX:+UseG1GC");
                    break;
                case "ZGC":
                    psi.ArgumentList.Add("-XX:+UseZGC");
                    break;
                case "Parallel":
                    psi.ArgumentList.Add("-XX:+UseParallelGC");
                    break;
                case "Serial":
                    psi.ArgumentList.Add("-XX:+UseSerialGC");
                    break;
            }

            // 禁用 IPv6：部分网络环境下 Java 会优先走 AAAA 记录而连不上服务器
            if (cfg.DisableIpv6)
                psi.ArgumentList.Add("-Djava.net.preferIPv4Stack=true");

            // 离线账户皮肤：把本地皮肤文件路径通过 JVM 系统属性传给游戏，
            // 配合支持自定义皮肤的加载器（CustomSkinLoader / SkinPort 等）在游戏内生效。
            // 注意：皮肤文件按「带连字符的 UUID」命名（与账户页保存规则一致）。
            if (!isMicrosoft && !string.IsNullOrEmpty(accUuid))
            {
                var offlineSkinPath = Path.Combine(AppContext.BaseDirectory, "res", $"{accUuid}_full.png");
                if (File.Exists(offlineSkinPath))
                {
                    psi.ArgumentList.Add($"-Dminecraft.skin.path={offlineSkinPath}");
                    psi.ArgumentList.Add($"-Dvbl.skin.path={offlineSkinPath}");
                    diag($"Offline skin arg: {offlineSkinPath}");
                }
                else
                {
                    diag("Offline skin: no local skin file, skip skin args");
                }
            }
            // 添加 JVM 参数，替换占位符，跳过 -cp ${classpath}（我们自己加）
            for (int i = 0; i < jvmArgs.Count; i++)
            {
                var arg = jvmArgs[i];
                if (arg == "-cp" && i + 1 < jvmArgs.Count && jvmArgs[i + 1].Contains("${classpath}"))
                {
                    i++; // 跳过 -cp 和 ${classpath}
                    continue;
                }
                arg = arg
                    .Replace("${natives_directory}", nativesDir)
                    .Replace("${launcher_name}", "VibrantbitLauncher")
                    .Replace("${launcher_version}", "1.0")
                    .Replace("${classpath}", classpath);
                psi.ArgumentList.Add(arg);
            }

            // 用户自定义 JVM 参数：一行一条，'#' 开头视为注释。
            // 放在 -cp 之前，这样用户可以用 -Dxxx=yyy 之类覆盖上面的默认值。
            foreach (var rawLine in (cfg.JvmArgs ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var custom = rawLine.Trim();
                if (custom.Length == 0 || custom.StartsWith("#"))
                    continue;

                psi.ArgumentList.Add(custom);
            }

            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(classpath);
            psi.ArgumentList.Add(mainClass);

            // 版本 JSON 里自带的窗体参数要先剔除：值是它的下一个元素，必须连值一起跳过，
            // 否则会留下一个游离的位置参数。剔除后统一由设置页的窗体大小决定。
            var cleanedGameArgs = new List<string>(gameArgs.Count);
            for (var i = 0; i < gameArgs.Count; i++)
            {
                var a = gameArgs[i];
                if (a is "--width" or "--height")
                {
                    i++;
                    continue;
                }
                if (a == "--fullscreen")
                    continue;

                cleanedGameArgs.Add(a);
            }

            var winW = Math.Clamp(cfg.WindowWidth, 320, 7680);
            var winH = Math.Clamp(cfg.WindowHeight, 240, 4320);

            foreach (var arg in cleanedGameArgs)
            {
                var replaced = arg
                    .Replace("${auth_player_name}", accName)
                    .Replace("${auth_uuid}", accUuid)
                    .Replace("${auth_access_token}", accToken)
                    .Replace("${version_name}", versionId)
                    .Replace("${game_directory}", gameDir)
                    .Replace("${assets_root}", Path.Combine(mcFolder, "assets"))
                    .Replace("${assets_index_name}", assetsIndex)
                    .Replace("${user_type}", accType)
                    .Replace("${version_type}", "release")
                    .Replace("${resolution_width}", winW.ToString())
                    .Replace("${resolution_height}", winH.ToString());
                psi.ArgumentList.Add(replaced);
            }

            // 窗体大小 / 全屏：1.13 以前的版本用的是老式 minecraftArguments，
            // 其 game 参数数组是空的（我们只补了空的 arguments.game），
            // 所以这两个参数必须由启动器显式传入。
            if (cfg.StartFullscreen)
            {
                psi.ArgumentList.Add("--fullscreen");
            }
            else
            {
                psi.ArgumentList.Add("--width");
                psi.ArgumentList.Add(winW.ToString());
                psi.ArgumentList.Add("--height");
                psi.ArgumentList.Add(winH.ToString());
            }
            diag($"Window: {(cfg.StartFullscreen ? "fullscreen" : $"{winW}x{winH}")}");

            // 版本隔离：显式添加 --gameDir 参数（确保游戏使用版本独立目录）
            if (!psi.ArgumentList.Contains("--gameDir"))
            {
                psi.ArgumentList.Add("--gameDir");
                psi.ArgumentList.Add(gameDir);
                diag($"Added --gameDir: {gameDir}");
            }

            // 授权与登录参数：根据账户类型添加
            var uuidNoHyphens = accUuid.Replace("-", "");
            void AddAuthArg(string name, string value)
            {
                if (!psi.ArgumentList.Contains(name))
                {
                    psi.ArgumentList.Add(name);
                    psi.ArgumentList.Add(value);
                }
            }
            AddAuthArg("--username", accName);
            AddAuthArg("--uuid", uuidNoHyphens);
            AddAuthArg("--userType", accType);
            if (isMicrosoft)
            {
                // 微软账户：添加认证参数
                AddAuthArg("--accessToken", accToken);
                diag($"Auth args: Microsoft account (username, uuid, accessToken, userType=msa)");
            }
            else
            {
                // 离线账户：只添加基础参数，不需要 accessToken/clientId/xuid
                // （皮肤已在上方通过 JVM 系统属性传入）
                diag($"Auth args: Offline account (username, uuid, userType=legacy)");
            }

            diag($"Total args: {psi.ArgumentList.Count}, first 3: {psi.FileName} {string.Join(" ", psi.ArgumentList.Take(3))}");

            var proc = new Process { StartInfo = psi };
            proc.Start();
            diag($"Process started: Id={proc.Id}");
            return proc;
        }

        private static string GetLibraryPath(JsonElement lib, string mcFolder)
        {
            if (lib.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("artifact", out var art))
            {
                if (art.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                    return Path.Combine(mcFolder, "libraries", path.GetString());
            }
            if (lib.TryGetProperty("name", out var name))
            {
                var parts = name.GetString().Split(':');
                if (parts.Length >= 3)
                {
                    var group = parts[0].Replace('.', '/');
                    return Path.Combine(mcFolder, "libraries", group, parts[1], parts[2], $"{parts[1]}-{parts[2]}.jar");
                }
            }
            return null;
        }

        /// <summary>
        /// 按设置页「主界面 · 启动后行为」对启动器自身执行动作。
        ///
        /// 取值：None（不动）/ Minimize（最小化）/ Close（关闭启动器）。
        /// 关闭走 <c>Window.Close()</c> 而不是 <c>AppLifetime.Shutdown()</c>：
        /// 前者会正常触发主窗口的关闭流程（托盘 / 退出清理），后者是硬退出。
        /// </summary>
        private void ApplyAfterLaunchAction()
        {
            var action = SettingsService.Current.AfterLaunchAction;
            if (string.IsNullOrWhiteSpace(action) || action.Equals("None", StringComparison.OrdinalIgnoreCase))
                return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var window = AppLifetime.MainWindow;
                    if (window is null)
                        return;

                    if (action.Equals("Minimize", StringComparison.OrdinalIgnoreCase))
                    {
                        window.WindowState = Avalonia.Controls.WindowState.Minimized;
                        Serilog.Log.Debug("[启动] 启动后行为：已最小化启动器");
                    }
                    else if (action.Equals("Close", StringComparison.OrdinalIgnoreCase))
                    {
                        Serilog.Log.Debug("[启动] 启动后行为：关闭启动器");
                        window.Close();
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "[启动] 启动后行为执行失败");
                }
            });
        }

        private void ShowError(string msg) =>
            _notification.Error(msg);

        private void ShowInfo(string msg) => _notification.Show(msg, "提示");

        private void ShowSuccess(string msg) => _notification.Success(msg);

        void writeLog(string time, string message)
        {
            using (StreamWriter sw = new StreamWriter("log.txt", true))
            {
                sw.WriteLine($"{time}: {message}");
            }
        }
    }
    public class LocalVersion
    {
        public string Version { get; set; }

        public RelayCommand<string> RunCommand { get; set; }
        public RelayCommand<string> SettingsCommand { get; set; }

    }
}
