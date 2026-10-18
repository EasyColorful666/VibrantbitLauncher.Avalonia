using System;
using System.Runtime.InteropServices;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>
    /// 运行环境探测：判断宿主系统版本、进程位数与硬件是否属于「低配」。
    ///
    /// 为什么要自己探测而不是用 <c>OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)</c>：
    /// 启动器要跑在 Windows 7 上，而 .NET 的 <c>Environment.OSVersion</c> 在没有
    /// 应用清单声明 supportedOS 的情况下，会把 Win8.1 及以上的系统**谎报成 6.2**，
    /// 导致 Win11 被误判。所以这里直接读注册表的 <c>CurrentBuildNumber</c>。
    /// </summary>
    public static class AppEnvironment
    {
        /// <summary>是否处于设计器预览模式（此时不做任何真实探测，一律按高配处理）。</summary>
        public static bool IsDesignMode { get; set; }

        /// <summary>宿主是否 Windows 7 / 8 / 8.1（无 Mica 材质，渲染后端也较弱）。</summary>
        public static bool IsLegacyWindows { get; private set; }

        /// <summary>是否 Windows 11（支持 Mica 材质）。</summary>
        public static bool IsWindows11 { get; private set; }

        /// <summary>进程是否为 32 位（用户态地址空间只有 2GB）。</summary>
        public static bool Is32BitProcess { get; private set; }

        /// <summary>物理内存总量（MB）。</summary>
        public static int TotalMemoryMb { get; private set; }

        /// <summary>逻辑处理器数量。</summary>
        public static int ProcessorCount { get; private set; }

        /// <summary>是否自动化探测判定为低配机器。</summary>
        public static bool IsLowEndHardware { get; private set; }

        /// <summary>探测结果的一句话描述，用于写日志 / 首次启动时提示用户。</summary>
        public static string Summary { get; private set; } = "未探测";

        private static bool _probed;

        /// <summary>
        /// 执行一次探测（幂等，重复调用直接返回）。
        /// 必须在使用任何依赖探测结果的逻辑之前调用，通常放在 <c>Main</c> 的最前面。
        /// </summary>
        public static void Probe()
        {
            if (_probed)
                return;

            _probed = true;

            try
            {
                ProcessorCount = Environment.ProcessorCount;
                Is32BitProcess = !Environment.Is64BitProcess;
                TotalMemoryMb = DetectTotalMemoryMb();

                var (major, minor, build) = DetectWindowsVersion();
                IsWindows11 = major >= 10 && build >= 22000;
                IsLegacyWindows = major < 10;

                // 自动化判定：任一条件命中即视为低配。
                // 门槛刻意放得比较宽（4 核 / 6GB），因为启动器自身在 2 核 + 核显的机器上
                // 打开就会明显掉帧，而这类机器在 Win7 用户里很常见。
                IsLowEndHardware =
                    Is32BitProcess
                    || IsLegacyWindows
                    || TotalMemoryMb > 0 && TotalMemoryMb < 6144
                    || ProcessorCount <= 3;

                Summary = string.Format(
                    "Windows {0}.{1} (Build {2}){3}，{4} 位进程，{5} 核，{6}",
                    major, minor, build,
                    IsWindows11 ? " [Win11]" : string.Empty,
                    Is32BitProcess ? "32" : "64",
                    ProcessorCount,
                    TotalMemoryMb > 0 ? TotalMemoryMb + " MB 内存" : "内存未知");
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "运行环境探测失败，按高配处理");
                IsLowEndHardware = false;
                Summary = "探测失败";
            }
        }

        /// <summary>
        /// 读注册表拿真实的 Windows 版本号。
        /// <c>Environment.OSVersion</c> 在未声明 supportedOS 的进程里会把系统谎报成 6.2，
        /// 这里从 <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion</c> 读
        /// <c>CurrentMajorVersionNumber</c> / <c>CurrentBuildNumber</c>（这两个键从 Win10 起才有，
        /// 老系统读不到就退回 <c>Environment.OSVersion</c>）。
        /// </summary>
        private static (int Major, int Minor, int Build) DetectWindowsVersion()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return (0, 0, 0);

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key is not null)
                {
                    var majorObj = key.GetValue("CurrentMajorVersionNumber");
                    var buildObj = key.GetValue("CurrentBuildNumber");

                    if (majorObj is not null && buildObj is not null
                        && int.TryParse(majorObj.ToString(), out var major)
                        && int.TryParse(buildObj.ToString(), out var build))
                    {
                        return (major, 0, build);
                    }
                }
            }
            catch
            {
                // 注册表被策略锁住等罕见情况：走下面的兜底
            }

            var fallback = Environment.OSVersion.Version;
            return (fallback.Major, fallback.Minor, fallback.Build);
        }

        /// <summary>
        /// 取物理内存总量（MB）。优先用 <c>GlobalMemoryStatusEx</c>：
        /// 它在所有 Windows 版本上都能用且不受 WMI 服务影响（老机器上 WMI 常常慢或不可用）。
        /// </summary>
        private static int DetectTotalMemoryMb()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return -1;

            try
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref status))
                    return (int)(status.ullTotalPhys / 1024 / 1024);
            }
            catch
            {
                // 忽略：返回 -1 表示未知
            }

            return -1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
