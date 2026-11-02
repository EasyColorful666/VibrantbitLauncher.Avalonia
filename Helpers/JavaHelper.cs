using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MinecraftLaunch.Base.Models.Game;
using MinecraftLaunch.Utilities;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>
    /// Java 查找。
    ///
    /// 不使用 MinecraftLaunch 的 <c>JavaUtil.EnumerableJavaAsync()</c>：它在遇到空路径候选时会
    /// 直接抛 ArgumentException，导致整个枚举中断、结果为空 —— 这正是设置页和开机向导
    /// 「检测不到 Java」的原因（机器上其实装了多个 JDK）。
    /// 这里改为自己收集候选路径并逐个解析，单项失败不影响其它项。
    ///
    /// 另外，探测任何候选之前都必须先过一遍静态体检（见 <see cref="IsUsableJava"/>）：
    /// Windows 的 PATH 里常混着 Oracle 的 javapath / java8path「转发器」——那是个只有几个
    /// exe、没有 java.dll 的目录，靠注册表里的 JavaHome 转发到真正的 JRE。
    /// 一旦注册表项被清空（Java 卸载不干净就会这样），执行转发器会弹出
    /// 「Error: could not find java.dll」/「Could not find Java SE Runtime Environment」
    /// 的**模态**对话框，探测线程被挂住，用户每进一次设置页 / 运行页都要点掉好几个框。
    /// 静态体检只读文件系统、不启动任何进程，能在执行之前就把这类转发器排除掉。
    /// </summary>
    public static class JavaHelper
    {
        /// <summary>单个候选的探测超时。超时即跳过，避免被半损坏的 Java 卡住整条枚举流程。</summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// 当前平台的 java 可执行文件名。
        ///
        /// <para>
        /// Linux / macOS 上**没有** java.exe / javaw.exe，就是 bin/java 这一个 ELF / Mach-O 文件。
        /// 早期这里处处硬写 <c>java.exe</c>，结果在 Linux 上每个候选路径都落在不存在的文件上，
        /// 静态体检全部不通过 —— 表现就是「装了 JDK 但检测不到」（1.0.30.2）。
        /// </para>
        /// </summary>
        public static string ExecutableName => OperatingSystem.IsWindows() ? "java.exe" : "java";

        /// <summary>
        /// Linux / macOS 上发行版约定的 JDK 根目录 —— 它们**下一层**就是 JDK 主目录
        /// （如 <c>/usr/lib/jvm/java-25-openjdk-amd64/bin/java</c>）。
        /// 与 <see cref="JavaRuntimeService"/> 里那份的区别是这里多了 <c>/usr/lib64</c> 与
        /// <c>/opt/openjdk</c>：扫描候选和「安装根目录」是两个不同的用途，不必强行共用。
        /// </summary>
        private static readonly string[] UnixJvmRoots =
        {
            "/usr/lib/jvm", "/usr/lib64/jvm", "/usr/java", "/opt/java", "/opt/jdk", "/opt/openjdk",
        };

        public static async Task<List<JavaEntry>> FindJavasAsync()
        {
            // 同一「主版本 + 位数」只留版本号最高的那个。
            //
            // 以前是「先到先得」：PATH / JAVA_HOME 里的旧补丁版排在候选序列前面，
            // 用户刚装好的新版被当成重复项扔掉 —— 表现就是「刚装完却检测不到」。
            // 现在同一主版本内比完整版本号，留下最新的。
            //
            // ⚠️ 这个列表只服务于「自动挑一个能启动的 Java」（GetAppropriateJava）。
            // 让用户自己挑的下拉框请用 FindAllJavasAsync() —— 那边一个安装一条，
            // 同一个主版本装了 17 / 21 / 25 也要全都列出来。
            var all = await FindAllJavasAsync().ConfigureAwait(false);

            return all
                .GroupBy(j => $"{ParseMajor(j.JavaVersion)}|{j.Is64bit}")
                .Select(g => g.OrderByDescending(j => VersionParts(j.JavaVersion), VersionPartComparer.Instance).First())
                .OrderByDescending(j => ParseMajor(j.JavaVersion))
                .ThenByDescending(j => VersionParts(j.JavaVersion), VersionPartComparer.Instance)
                .ToList();
        }

        /// <summary>
        /// 列出机器上**每一个**可用的 Java 安装（不按主版本合并）。
        ///
        /// <para>
        /// 去重口径是「bin 目录」而不是「主版本 + 位数」：同一个 JDK 里的 java.exe 与
        /// javaw.exe 是两个真实文件，却显然是同一个安装 —— 早期按路径去重会让下拉框里
        /// 出现成对重复项。同一目录内优先保留 javaw.exe（启动游戏不弹控制台窗口）。
        /// </para>
        /// </summary>
        public static async Task<List<JavaEntry>> FindAllJavasAsync()
        {
            var collected = new List<JavaEntry>();
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // key = bin 目录（真实路径），value = (是否为 javaw, 序号)
            var byHome = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var candidateCount = 0;
            var usableCount = 0;

            foreach (var candidate in EnumerateCandidates())
            {
                candidateCount++;

                if (string.IsNullOrWhiteSpace(candidate) || !tried.Add(candidate))
                    continue;

                // 静态体检：转发器 shim、0 字节占位文件、被拆散的目录在这里就被排除，绝不执行
                if (!IsUsableJava(candidate))
                    continue;

                usableCount++;

                try
                {
                    // 探测进程有超时保护：即使某个 Java 仍会弹窗，也只损失这一项，不会卡住枚举
                    var info = await ProbeEntryAsync(candidate).ConfigureAwait(false);
                    if (info is null || string.IsNullOrWhiteSpace(info.JavaPath))
                        continue;

                    var home = HomeOf(info.JavaPath);
                    if (home.Length == 0)
                        continue;

                    var isJavaw = string.Equals(
                        Path.GetFileName(info.JavaPath), "javaw.exe", StringComparison.OrdinalIgnoreCase);

                    if (byHome.TryGetValue(home, out var existing))
                    {
                        // 同一个安装已经收过一条：只在「原来那条是 java.exe、这条是 javaw.exe」时替换
                        var current = collected[existing];
                        var currentIsJavaw = string.Equals(
                            Path.GetFileName(current.JavaPath), "javaw.exe", StringComparison.OrdinalIgnoreCase);

                        if (isJavaw && !currentIsJavaw)
                            collected[existing] = info;

                        continue;
                    }

                    byHome[home] = collected.Count;
                    collected.Add(info);
                }
                catch (Exception ex)
                {
                    // 单个候选不可用（损坏、版本过低、非 Java 可执行文件）时跳过，
                    // 但留一条 Debug 日志 —— 反复「检测不到」时要能顺着日志查到底卡在哪一步
                    Serilog.Log.Debug(ex, "Java 探测失败，已跳过：{Path}", candidate);
                }
            }

            var result = collected
                .OrderByDescending(j => ParseMajor(j.JavaVersion))
                .ThenByDescending(j => VersionParts(j.JavaVersion), VersionPartComparer.Instance)
                .ThenBy(j => j.JavaPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Serilog.Log.Information(
                "Java 查找：候选 {Candidates} → 体检通过 {Usable} → 探测成功 {Collected} → 安装数 {Count}｜{List}",
                candidateCount, usableCount, collected.Count, result.Count,
                string.Join(" | ", result.Select(j => $"{j.JavaVersion} ({j.JavaPath})")));

            return result;
        }

        /// <summary>java.exe 所在的 bin 目录（真实路径）；取不到时返回空串。</summary>
        private static string HomeOf(string javaPath)
        {
            try
            {
                var real = ResolveRealPath(javaPath);
                return (Path.GetDirectoryName(real) ?? string.Empty).TrimEnd('\\', '/');
            }
            catch
            {
                return string.Empty;
            }
        }


        /// <summary>
        /// 把版本号拆成可比较的数字段：<c>"21.0.12+101"</c> → [21, 0, 12, 101]。
        /// <c>"1.8.0_441"</c> 会先去掉开头的 1（与 <c>8.0.441</c> 归一），
        /// 免得同一主版本组里两种写法比大小比错。
        /// </summary>
        private static int[] VersionParts(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return Array.Empty<int>();

            var parts = Regex.Matches(version, @"\d+")
                .Select(m => int.TryParse(m.Value, out var v) ? v : 0)
                .ToList();

            if (parts.Count > 1 && parts[0] == 1)
                parts.RemoveAt(0);

            return parts.ToArray();
        }

        /// <summary>按数字段逐位比较版本号，缺位补 0（21.0 与 21.0.0 视为相同）。</summary>
        private sealed class VersionPartComparer : IComparer<int[]>
        {
            public static readonly VersionPartComparer Instance = new();

            public int Compare(int[]? x, int[]? y)
            {
                x ??= Array.Empty<int>();
                y ??= Array.Empty<int>();

                var length = Math.Max(x.Length, y.Length);
                for (var i = 0; i < length; i++)
                {
                    var left = i < x.Length ? x[i] : 0;
                    var right = i < y.Length ? y[i] : 0;
                    if (left != right)
                        return left.CompareTo(right);
                }

                return 0;
            }
        }

        /// <summary>只找第一个可用 Java 的路径（安装器等场景用）。</summary>
        public static async Task<string?> FindFirstJavaPathAsync()
        {
            var javas = await FindJavasAsync();
            return javas.FirstOrDefault()?.JavaPath;
        }

        /// <summary>
        /// 探测单个 java.exe：静态体检 + 跑一次 <c>-version</c>，拿到版本号等信息。
        ///
        /// <para>
        /// 用于「用户手工指定的路径」。这类路径不在扫描候选里，不探测的话
        /// 下拉框只能显示路径、版本号一栏是空的，看起来像坏数据。
        /// 探测失败（文件不可用 / 超时 / 非 Java）返回 null，由调用方决定怎么退化。
        /// </para>
        /// </summary>
        public static async Task<JavaEntry?> ProbeJavaAsync(string? exePath)
        {
            if (!IsUsableJava(exePath))
                return null;

            return await ProbeEntryAsync(exePath!).ConfigureAwait(false);
        }

        /// <summary>
        /// 跑一次 <c>java -version</c> 拿版本信息，带超时；失败返回 null。
        ///
        /// <para>
        /// 先走 MinecraftLaunch 的 <see cref="JavaUtil"/>（Windows 上一贯如此）；
        /// 拿不到结果且不在 Windows 上时，再用自己那套 <c>-XshowSettings:properties -version</c>
        /// 兜底 —— 那个库的枚举部分是按 Windows 写的，探测部分能不能在 Linux 上跑通
        /// 不该成为「检测不到 Java」的单点故障。
        /// </para>
        /// </summary>
        private static async Task<JavaEntry?> ProbeEntryAsync(string exePath)
        {
            try
            {
                var probe = JavaUtil.GetJavaInfoAsync(exePath);
                if (await Task.WhenAny(probe, Task.Delay(ProbeTimeout)) != probe)
                {
                    // 以前这里是静默 continue：用户只知道「检测不到」，日志里一片空白
                    Serilog.Log.Warning("Java 探测超时，已跳过：{Path}", exePath);
                }
                else
                {
                    var info = await probe;
                    if (info is not null && !string.IsNullOrWhiteSpace(info.JavaPath))
                        return info;
                }
            }
            catch (Exception ex)
            {
                // 单个候选不可用（损坏、版本过低、非 Java 可执行文件）时跳过，
                // 但留一条 Debug 日志 —— 反复「检测不到」时要能顺着日志查到底卡在哪一步
                Serilog.Log.Debug(ex, "Java 探测失败，已跳过：{Path}", exePath);
            }

            if (OperatingSystem.IsWindows())
                return null;

            return await ProbeUnixJavaAsync(exePath).ConfigureAwait(false);
        }

        /// <summary>
        /// 非 Windows 平台的探测：直接跑 <c>-XshowSettings:properties -version</c>，
        /// 从输出里读 <c>java.version</c> 与 <c>sun.arch.data.model</c>。
        ///
        /// <para>
        /// 版本号信息 java 是打到 **stderr** 的，属性表打到 stdout，两个流都要读 ——
        /// 只读其中一个会得到「有版本没位数」或者干脆一片空白。
        /// </para>
        /// </summary>
        private static async Task<JavaEntry?> ProbeUnixJavaAsync(string exePath)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exePath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                psi.ArgumentList.Add("-XshowSettings:properties");
                psi.ArgumentList.Add("-version");

                using var process = System.Diagnostics.Process.Start(psi);
                if (process is null)
                    return null;

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource(ProbeTimeout);
                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* 已经退出 */ }
                    Serilog.Log.Warning("Java 探测超时，已跳过：{Path}", exePath);
                    return null;
                }

                var outText = await stdout.ConfigureAwait(false);
                var errText = await stderr.ConfigureAwait(false);
                var text = outText + "\n" + errText;

                var version = Regex.Match(
                    text, @"^\s*java\.version\s*=\s*(\S+)",
                    RegexOptions.Multiline).Groups[1].Value;

                if (string.IsNullOrWhiteSpace(version))
                {
                    // 老版本 / 被裁剪的运行时可能没有属性表，退回到 banner 行：
                    // openjdk version "25.0.1" 2025-10-21
                    version = Regex.Match(text, @"(?:java|openjdk) version ""([^""]+)""").Groups[1].Value;
                }

                if (string.IsNullOrWhiteSpace(version))
                    return null;

                var model = Regex.Match(
                    text, @"^\s*sun\.arch\.data\.model\s*=\s*(\d+)",
                    RegexOptions.Multiline).Groups[1].Value;

                bool is64;

                if (model.Length > 0)
                {
                    is64 = model == "64";
                }
                else
                {
                    var arch = Regex.Match(
                        text, @"^\s*os\.arch\s*=\s*(\S+)",
                        RegexOptions.Multiline).Groups[1].Value;

                    is64 = arch.Contains("64", StringComparison.Ordinal)
                        || arch.StartsWith("aarch", StringComparison.OrdinalIgnoreCase);
                }

                return new JavaEntry
                {
                    JavaPath = exePath,
                    JavaVersion = version.Trim(),
                    Is64bit = is64,
                };
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "Java 探测失败（Unix）：{Path}", exePath);
                return null;
            }
        }

        /// <summary>版本号 → 主版本（<c>1.8.0_441</c> → 8，<c>21.0.3</c> → 21）。</summary>
        public static int MajorOf(string? version) => ParseMajor(version);


        /// <summary>
        /// 判断一个 java 可执行文件是否来自**完整**的 Java 安装，即能不能直接拿来启动。
        ///
        /// <para>
        /// Windows 判据：同目录存在 java.dll（真实 JDK / JRE / Minecraft 自带运行时的 bin 目录都有），
        /// 或者相邻的 <c>server\jvm.dll</c> / <c>client\jvm.dll</c> 存在。
        /// Oracle 的 javapath / java8path 转发器目录里只有几个 exe，会被判为不可用。
        /// </para>
        ///
        /// <para>
        /// Linux / macOS 判据：<c>bin/java</c> 的兄弟目录 <c>../lib/server/libjvm.so</c>（macOS 是
        /// <c>libjvm.dylib</c>）存在 —— 这两块系统根本没有 java.dll，照抄 Windows 判据会让
        /// 每个候选都「体检不通过」，用户看到的就是「装了 JDK 却检测不到 Java」（1.0.30.2）。
        /// 而且 <c>/usr/bin/java</c> 是软链，必须先解开再判断。
        /// </para>
        ///
        /// 这个方法是纯文件系统检查，**不会启动任何进程** —— 这一点是刻意的：
        /// 转发器一旦被执行就会弹模态错误框，把调用方（UI 线程或探测循环）挂住。
        /// </summary>
        public static bool IsUsableJava(string? exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath))
                return false;

            try
            {
                var file = new FileInfo(exePath);
                if (!file.Exists)
                    return false;

                // Windows 上的转发器 shim 只有几 KB；Linux 的 bin/java 是十几 KB 的正常 ELF，
                // 但发行版裁剪后可能更小，所以体积下限只在 Windows 上生效。
                if (OperatingSystem.IsWindows() && file.Length < 4096)   // 0 字节 / 被截断的占位文件
                    return false;

                var dir = file.DirectoryName;
                if (string.IsNullOrEmpty(dir))
                    return false;

                if (!OperatingSystem.IsWindows())
                    return LooksLikeUnixJava(exePath);

                // 已知的转发器目录：不看内容，直接排除
                if (IsShimDirectory(dir))
                    return false;

                return File.Exists(Path.Combine(dir, "java.dll"))
                    || File.Exists(Path.Combine(dir, "jvm.dll"))
                    || File.Exists(Path.Combine(dir, "server", "jvm.dll"))
                    || File.Exists(Path.Combine(dir, "client", "jvm.dll"));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Linux / macOS 的可执行文件后面是否真有一份 JVM。
        ///
        /// <para>
        /// 先解开软链：<c>/usr/bin/java → /etc/alternatives/java → /usr/lib/jvm/&lt;x&gt;/bin/java</c>。
        /// 然后看 JDK 主目录下的 <c>lib/server/libjvm.so</c>（JDK 8 的 jre 布局另算）；
        /// 最后用 <c>release</c> 文件兜底 —— 每个正规 JDK 都有，且它存在基本等于「这是一份完整 JDK」。
        /// </para>
        /// </summary>
        private static bool LooksLikeUnixJava(string exePath)
        {
            var real = ResolveRealPath(exePath);
            var bin = Path.GetDirectoryName(real);
            if (string.IsNullOrEmpty(bin))
                return false;

            var home = Path.GetDirectoryName(bin);
            if (string.IsNullOrEmpty(home))
                return false;

            var libName = OperatingSystem.IsMacOS() ? "libjvm.dylib" : "libjvm.so";

            var candidates = new[]
            {
                Path.Combine(home, "lib", "server", libName),
                Path.Combine(home, "lib", "client", libName),
                Path.Combine(home, "lib", libName),
                Path.Combine(home, "jre", "lib", "server", libName),
                Path.Combine(home, "jre", "lib", "client", libName),
            };

            if (candidates.Any(File.Exists))
                return true;

            // JDK 8 的老布局：jre/lib/<arch>/server/libjvm.so
            var jreLib = Path.Combine(home, "jre", "lib");
            if (Directory.Exists(jreLib))
            {
                foreach (var arch in SafeDirectories(jreLib))
                {
                    if (File.Exists(Path.Combine(arch, "server", libName))
                        || File.Exists(Path.Combine(arch, "client", libName)))
                        return true;
                }
            }

            return File.Exists(Path.Combine(home, "release"));
        }

        /// <summary>
        /// PATH 里常见的 Java 转发器目录。
        /// 它们存放的是「转发到注册表所指 JRE」的 shim，本身不含运行时。
        /// </summary>
        private static bool IsShimDirectory(string directory)
        {
            var normalized = directory.Replace('/', '\\');

            return normalized.Contains(@"\Oracle\Java\javapath", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(@"\Oracle\Java\java8path", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(@"\Common Files\Oracle\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(@"\Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveRealPath(string path)
        {
            try
            {
                return new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private static int ParseMajor(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return 0;

            var match = Regex.Match(version, @"\d+");
            if (!match.Success)
                return 0;

            var major = int.Parse(match.Value);
            return major == 1 ? 8 : major;   // "1.8.0_441" → 8
        }

        private static IEnumerable<string> EnumerateCandidates()
        {
            foreach (var exe in FromJavaHome()) yield return exe;
            foreach (var exe in FromPathVariable()) yield return exe;
            foreach (var exe in FromRegistry()) yield return exe;
            foreach (var exe in FromConfiguredRuntimeRoot()) yield return exe;
            foreach (var exe in FromCommonFolders()) yield return exe;
            foreach (var exe in FromUnixLocations()) yield return exe;
            foreach (var exe in FromMinecraftRuntime()) yield return exe;
        }

        /// <summary>
        /// 从注册表读 JDK / JRE 的安装位置。
        ///
        /// <para>
        /// 目录扫描只能覆盖「装在默认厂商目录下」的情况；用户把 JDK 放到
        /// <c>D:\Dev\jdk</c> 这种自选目录时只有注册表知道。各家写的键不一样：
        /// </para>
        /// <list type="bullet">
        ///   <item>Oracle / OpenJDK：<c>SOFTWARE\JavaSoft\Java Development Kit\&lt;ver&gt;\JavaHome</c></item>
        ///   <item>Adoptium MSI：<c>SOFTWARE\Eclipse Adoptium\JDK\&lt;ver&gt;\path</c></item>
        ///   <item>Microsoft Build of OpenJDK：<c>SOFTWARE\Microsoft\JDK\&lt;ver&gt;\JavaHome</c></item>
        /// </list>
        /// <para>
        /// 注册表读不了（非 Windows、权限受限）就安静跳过 —— 这一条只是「多找到几个」，
        /// 失败不该影响其它候选。
        /// </para>
        /// </summary>
        private static IEnumerable<string> FromRegistry()
        {
            // 注册表只有 Windows 有。Linux / macOS 上 Registry.LocalMachine 会抛
            // PlatformNotSupportedException —— 下面虽有 try/catch 兜着，但那是「每扫一次
            // 就吞一次异常」的无谓噪音，直接按平台跳过。
            if (!OperatingSystem.IsWindows())
                return Array.Empty<string>();

            var results = new List<string>();

            void Scan(string subKey, string valueName)
            {
                try
                {
                    using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
                    if (root is null)
                        return;

                    foreach (var name in root.GetSubKeyNames())
                    {
                        using var version = root.OpenSubKey(name);
                        var home = version?.GetValue(valueName) as string;
                        if (string.IsNullOrWhiteSpace(home))
                            continue;

                        home = home.Trim().Trim('"');
                        results.AddRange(JavaIn(home));
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug(ex, "读取注册表 {Key} 失败，已跳过", subKey);
                }
            }

            foreach (var prefix in new[] { @"SOFTWARE\", @"SOFTWARE\WOW6432Node\" })
            {
                Scan(prefix + @"JavaSoft\Java Development Kit", "JavaHome");
                Scan(prefix + @"JavaSoft\Java Runtime Environment", "JavaHome");
                Scan(prefix + @"Eclipse Adoptium\JDK", "path");
                Scan(prefix + @"Eclipse Adoptium\JRE", "path");
                Scan(prefix + @"Microsoft\JDK", "JavaHome");
            }

            return results;
        }

        /// <summary>所有可枚举的固定磁盘根目录（U 盘 / 光驱 / 网络盘跳过 —— 扫它们只会拖慢启动）。</summary>
        private static IEnumerable<string> SafeFixedDrives()
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return Array.Empty<string>(); }

            var roots = new List<string>();

            foreach (var drive in drives)
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                        continue;

                    roots.Add(drive.RootDirectory.FullName);
                }
                catch
                {
                    // 某个盘符不可访问（已拔出 / 权限）时跳过它
                }
            }

            return roots;
        }

        /// <summary>
        /// 用户在「Java 下载」页指定的安装根目录。
        ///
        /// <para>
        /// 这条候选是「装完检测不到」的主因修补：那条链路把 JDK 装到
        /// <c>{用户选的根}\jdk-xx-hotspot</c>，而这里以前只认 Program Files 那套固定目录。
        /// 用户要是把根目录改成 D:\Java（很常见，系统盘小），装完在设置页就彻底找不到。
        /// </para>
        ///
        /// <para>
        /// 扫描深度按目录结构来：根目录本身（<c>D:\Java\bin\java.exe</c>）、
        /// 一层子目录（<c>Eclipse Adoptium\jdk-21.0.12.1-hotspot\bin</c>），
        /// 以及两层子目录（少数安装器会多套一层版本号）。
        /// </para>
        /// </summary>
        private static IEnumerable<string> FromConfiguredRuntimeRoot()
        {
            string root;
            try
            {
                root = JavaRuntimeService.ResolveRuntimeRoot();
            }
            catch
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                yield break;

            foreach (var exe in JavaIn(root))
                yield return exe;

            foreach (var sub in SafeDirectories(root))
            {
                foreach (var exe in JavaIn(sub))
                    yield return exe;

                foreach (var sub2 in SafeDirectories(sub))
                    foreach (var exe in JavaIn(sub2))
                        yield return exe;
            }
        }

        /// <summary>某个 Java 主目录下的可执行文件，即 <c>&lt;home&gt;/bin/java[.exe]</c>。</summary>
        private static IEnumerable<string> JavaIn(string home) => JavaInBin(Path.Combine(home, "bin"));

        /// <summary>
        /// 某个 bin 目录下的可执行文件。
        /// Windows 先给 javaw.exe（启动游戏不弹控制台），再给 java.exe；
        /// 其它平台只有 java 一个名字。
        /// </summary>
        private static IEnumerable<string> JavaInBin(string bin)
        {
            if (OperatingSystem.IsWindows())
                yield return Path.Combine(bin, "javaw.exe");

            yield return Path.Combine(bin, ExecutableName);
        }

        private static IEnumerable<string> FromJavaHome()
        {
            var home = Environment.GetEnvironmentVariable("JAVA_HOME")?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(home))
                yield break;

            foreach (var exe in JavaIn(home))
                yield return exe;
        }

        private static IEnumerable<string> FromPathVariable()
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
                yield break;

            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = dir.Trim().Trim('"');
                if (trimmed.Length == 0)
                    continue;

                // PATH 里放的是 bin 目录本身，不是 JDK 主目录
                foreach (var exe in JavaInBin(trimmed))
                    yield return exe;
            }
        }

        /// <summary>
        /// 各家 JDK 的默认安装根目录：扫根目录本身 + 一层子目录。
        ///
        /// <para>
        /// Program Files 的路径**不能硬编码成 C:\** —— 系统盘不是 C、或 Program Files
        /// 被挪到别的盘时，从前那串常量会让整片候选目录全部落空，
        /// 用户装好的 JDK 一个都扫不到。这里统一从 <see cref="Environment.SpecialFolder"/> 取。
        /// </para>
        /// </summary>
        private static IEnumerable<string> FromCommonFolders()
        {
            // 这一整段都是 Windows 的目录约定（Program Files / LOCALAPPDATA\Programs）。
            // Linux / macOS 的约定位置见 FromUnixLocations()。
            if (!OperatingSystem.IsWindows())
                yield break;

            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

            var roots = new List<string>();

            void AddProgramFiles(Environment.SpecialFolder folder, params string[] vendors)
            {
                var basePath = Environment.GetFolderPath(folder);
                if (string.IsNullOrWhiteSpace(basePath))
                    return;

                foreach (var vendor in vendors)
                    roots.Add(Path.Combine(basePath, vendor));
            }

            AddProgramFiles(Environment.SpecialFolder.ProgramFiles,
                "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft",
                "Zulu", "Amazon Corretto", "BellSoft", "Semeru", "SapMachine", "JetBrains");

            AddProgramFiles(Environment.SpecialFolder.ProgramFilesX86,
                "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft",
                "Zulu", "Amazon Corretto", "BellSoft");

            // 所有固定磁盘的 Program Files。
            //
            // `Environment.SpecialFolder.ProgramFiles` 只给**系统盘**那一个目录，
            // 而用户把 JDK 装在 D:\Program Files\... 是常态（本机就有
            // D:\Program Files\Java\jdk-22 与 D:\Program Files\Eclipse Adoptium\jdk-25.0.4.1）。
            // 以前这些安装整片扫不到，表现就是「装了 8 个 Java，启动器只列出 4 个」。
            var vendors = new[]
            {
                "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft",
                "Zulu", "Amazon Corretto", "BellSoft", "Semeru", "SapMachine", "JetBrains",
            };

            foreach (var drive in SafeFixedDrives())
            {
                foreach (var programFiles in new[] { "Program Files", "Program Files (x86)" })
                {
                    var basePath = Path.Combine(drive, programFiles);
                    if (!Directory.Exists(basePath))
                        continue;

                    foreach (var vendor in vendors)
                        roots.Add(Path.Combine(basePath, vendor));
                }
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
                roots.Add(Path.Combine(localAppData, "Programs", "Eclipse Adoptium"));

            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var exe in JavaIn(root))
                    yield return exe;

                foreach (var sub in SafeDirectories(root))
                    foreach (var exe in JavaIn(sub))
                        yield return exe;
            }
        }

        /// <summary>.minecraft 下由启动器下载的 Java 运行时。</summary>
        private static IEnumerable<string> FromMinecraftRuntime()
        {
            string full;
            try
            {
                full = Path.GetFullPath(SettingsService.ResolveMinecraftFolder());
            }
            catch
            {
                yield break;
            }

            var runtimeDir = Path.Combine(full, "runtime");
            if (!Directory.Exists(runtimeDir))
                yield break;

            // 官方启动器的运行时目录里，Windows 只有 javaw.exe（不弹控制台），
            // 其它平台就是 bin/java。
            var fileName = OperatingSystem.IsWindows() ? "javaw.exe" : "java";

            foreach (var runtime in SafeDirectories(runtimeDir))
                foreach (var exe in SafeFiles(runtime, fileName, 4))
                    yield return exe;
        }

        /// <summary>
        /// Linux / macOS 上 JDK 的常见落点（1.0.30.2）。
        ///
        /// <para>
        /// 这两块系统没有「Program Files + 厂商子目录」那套约定，包管理器把 JDK 装在
        /// <c>/usr/lib/jvm</c>，sdkman 装在 <c>~/.sdkman/candidates/java</c>，
        /// Homebrew 装在 <c>/opt/homebrew/opt/openjdk*</c>，macOS 的 Oracle/Temurin 安装器
        /// 落在 <c>/Library/Java/JavaVirtualMachines/&lt;x&gt;.jdk/Contents/Home</c>。
        /// 不扫这些目录，用户用系统包管理器装完 JDK，界面永远是「没有检测到 Java」。
        /// </para>
        /// </summary>
        private static IEnumerable<string> FromUnixLocations()
        {
            if (OperatingSystem.IsWindows())
                yield break;

            // 1) 发行版约定的 JDK 根目录（下一层就是 JDK 主目录）
            foreach (var root in UnixJvmRoots)
                foreach (var exe in JavaHomesUnder(root, depth: 0))
                    yield return exe;

            // 2) 用户目录下的版本管理器：sdkman / 手动解压的 jdks / 我们自己的 runtime
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                foreach (var exe in JavaHomesUnder(
                             Path.Combine(home, ".sdkman", "candidates", "java"), depth: 0))
                    yield return exe;

                foreach (var exe in JavaHomesUnder(Path.Combine(home, ".jdks"), depth: 0))
                    yield return exe;

                foreach (var exe in JavaHomesUnder(
                             Path.Combine(home, ".local", "share", "VibrantbitLauncher", "runtime"), depth: 0))
                    yield return exe;
            }

            // 3) macOS：/Library/Java/JavaVirtualMachines/<x>.jdk/Contents/Home/bin/java
            foreach (var exe in JavaHomesUnder("/Library/Java/JavaVirtualMachines", depth: 2))
                yield return exe;

            // 4) Homebrew（Apple Silicon 在 /opt/homebrew，Intel 在 /usr/local）
            foreach (var prefix in new[] { "/opt/homebrew/opt", "/usr/local/opt" })
            {
                foreach (var dir in SafeDirectories(prefix))
                {
                    if (!Path.GetFileName(dir).StartsWith("openjdk", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // formula：<prefix>/openjdk@21/bin/java
                    foreach (var exe in JavaIn(dir))
                        yield return exe;

                    // cask：<prefix>/openjdk/libexec/openjdk.jdk/Contents/Home/bin/java
                    foreach (var exe in JavaIn(Path.Combine(dir, "libexec", "openjdk.jdk", "Contents", "Home")))
                        yield return exe;
                }
            }
        }

        /// <summary>
        /// 在 <paramref name="root"/> 下按「JDK 主目录」的层级找可执行文件。
        /// <paramref name="depth"/> 是还允许往下钻的层数：<c>0</c> 表示根目录的直接子目录就是 JDK 主目录。
        /// </summary>
        private static IEnumerable<string> JavaHomesUnder(string root, int depth)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                yield break;

            // 根目录本身可能就是 JDK 主目录（用户把 JAVA_HOME 指到这里）
            foreach (var exe in JavaIn(root))
                yield return exe;

            foreach (var sub in SafeDirectories(root))
            {
                if (depth > 0)
                {
                    foreach (var nested in JavaHomesUnder(sub, depth - 1))
                        yield return nested;

                    continue;
                }

                foreach (var exe in JavaIn(sub))
                    yield return exe;
            }
        }

        private static IEnumerable<string> SafeDirectories(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch { return Array.Empty<string>(); }
        }

        private static IEnumerable<string> SafeFiles(string dir, string fileName, int maxDepth)
        {
            try
            {
                return Directory.EnumerateFiles(dir, fileName, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = maxDepth,
                    IgnoreInaccessible = true
                }).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
