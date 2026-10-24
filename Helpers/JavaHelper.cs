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
                    var probe = JavaUtil.GetJavaInfoAsync(candidate);
                    if (await Task.WhenAny(probe, Task.Delay(ProbeTimeout)) != probe)
                    {
                        // 以前这里是静默 continue：用户只知道「检测不到」，日志里一片空白
                        Serilog.Log.Warning("Java 探测超时，已跳过：{Path}", candidate);
                        continue;
                    }

                    var info = await probe;
                    if (info == null || string.IsNullOrWhiteSpace(info.JavaPath))
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

            try
            {
                var probe = JavaUtil.GetJavaInfoAsync(exePath!);
                if (await Task.WhenAny(probe, Task.Delay(ProbeTimeout)) != probe)
                {
                    Serilog.Log.Warning("Java 探测超时：{Path}", exePath);
                    return null;
                }

                return await probe;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "Java 探测失败：{Path}", exePath);
                return null;
            }
        }

        /// <summary>版本号 → 主版本（<c>1.8.0_441</c> → 8，<c>21.0.3</c> → 21）。</summary>
        public static int MajorOf(string? version) => ParseMajor(version);


        /// <summary>
        /// 判断一个 java.exe / javaw.exe 是否来自**完整**的 Java 安装，即能不能直接拿来启动。
        ///
        /// 判据：同目录存在 java.dll（真实 JDK / JRE / Minecraft 自带运行时的 bin 目录都有），
        /// 或者相邻的 <c>server\jvm.dll</c> / <c>client\jvm.dll</c> 存在。
        /// Oracle 的 javapath / java8path 转发器目录里只有几个 exe，会被判为不可用。
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
                if (!file.Exists || file.Length < 4096)   // 0 字节 / 被截断的占位文件
                    return false;

                var dir = file.DirectoryName;
                if (string.IsNullOrEmpty(dir))
                    return false;

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
                        results.Add(Path.Combine(home, "bin", "javaw.exe"));
                        results.Add(Path.Combine(home, "bin", "java.exe"));
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

            foreach (var exe in JavawAndJavaIn(root))
                yield return exe;

            foreach (var sub in SafeDirectories(root))
            {
                foreach (var exe in JavawAndJavaIn(sub))
                    yield return exe;

                foreach (var sub2 in SafeDirectories(sub))
                    foreach (var exe in JavawAndJavaIn(sub2))
                        yield return exe;
            }
        }

        /// <summary>某个 Java 主目录下的 bin\javaw.exe 与 bin\java.exe。</summary>
        private static IEnumerable<string> JavawAndJavaIn(string home)
        {
            yield return Path.Combine(home, "bin", "javaw.exe");
            yield return Path.Combine(home, "bin", "java.exe");
        }

        private static IEnumerable<string> FromJavaHome()
        {
            var home = Environment.GetEnvironmentVariable("JAVA_HOME")?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(home))
                yield break;

            yield return Path.Combine(home, "bin", "javaw.exe");
            yield return Path.Combine(home, "bin", "java.exe");
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

                yield return Path.Combine(trimmed, "javaw.exe");
                yield return Path.Combine(trimmed, "java.exe");
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

                foreach (var exe in JavawAndJavaIn(root))
                    yield return exe;

                foreach (var sub in SafeDirectories(root))
                    foreach (var exe in JavawAndJavaIn(sub))
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

            foreach (var runtime in SafeDirectories(runtimeDir))
                foreach (var exe in SafeFiles(runtime, "javaw.exe", 4))
                    yield return exe;
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
