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
            var result = new List<JavaEntry>();
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var acceptedPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in EnumerateCandidates())
            {
                if (string.IsNullOrWhiteSpace(candidate) || !tried.Add(candidate))
                    continue;

                // 静态体检：转发器 shim、0 字节占位文件、被拆散的目录在这里就被排除，绝不执行
                if (!IsUsableJava(candidate))
                    continue;

                try
                {
                    // 探测进程有超时保护：即使某个 Java 仍会弹窗，也只损失这一项，不会卡住枚举
                    var probe = JavaUtil.GetJavaInfoAsync(candidate);
                    if (await Task.WhenAny(probe, Task.Delay(ProbeTimeout)) != probe)
                        continue;

                    var info = await probe;
                    if (info == null || string.IsNullOrWhiteSpace(info.JavaPath))
                        continue;

                    // 同一份 Java 常常同时以 javaw.exe / java.exe、以及 Oracle 的 javapath shim 出现，
                    // 按「主版本 + 位数」去重，避免下拉框里出现一串重复项
                    var key = $"{ParseMajor(info.JavaVersion)}|{info.Is64bit}";
                    if (!accepted.Add(key))
                        continue;

                    // 另外按真实路径去重（符号链接如 ...\Java\latest 会指向同一个 java.exe）
                    if (!acceptedPath.Add(ResolveRealPath(info.JavaPath)))
                        continue;

                    result.Add(info);
                }
                catch
                {
                    // 单个候选不可用（损坏、版本过低、非 Java 可执行文件）时跳过
                }
            }

            // 主版本号高的排前面
            return result.OrderByDescending(j => ParseMajor(j.JavaVersion)).ToList();
        }

        /// <summary>只找第一个可用 Java 的路径（安装器等场景用）。</summary>
        public static async Task<string?> FindFirstJavaPathAsync()
        {
            var javas = await FindJavasAsync();
            return javas.FirstOrDefault()?.JavaPath;
        }

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
            foreach (var exe in FromCommonFolders()) yield return exe;
            foreach (var exe in FromMinecraftRuntime()) yield return exe;
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

        /// <summary>各家 JDK 的默认安装根目录：扫根目录本身 + 一层子目录。</summary>
        private static IEnumerable<string> FromCommonFolders()
        {
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

            var roots = new List<string>
            {
                @"C:\Program Files\Java",
                @"C:\Program Files (x86)\Java",
                @"C:\Program Files\Eclipse Adoptium",
                @"C:\Program Files\Eclipse Foundation",
                @"C:\Program Files\Microsoft",
                @"C:\Program Files\Zulu",
                @"C:\Program Files\Amazon Corretto",
                @"C:\Program Files\BellSoft",
                @"C:\Program Files\Semeru",
                @"C:\Program Files\SapMachine",
                @"C:\Program Files\JetBrains",
            };

            if (!string.IsNullOrWhiteSpace(localAppData))
                roots.Add(Path.Combine(localAppData, "Programs", "Eclipse Adoptium"));

            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                    continue;

                yield return Path.Combine(root, "bin", "javaw.exe");
                yield return Path.Combine(root, "bin", "java.exe");

                foreach (var sub in SafeDirectories(root))
                {
                    yield return Path.Combine(sub, "bin", "javaw.exe");
                    yield return Path.Combine(sub, "bin", "java.exe");
                }
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
