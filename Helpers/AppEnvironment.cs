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

        /// <summary>
        /// 是否运行在虚拟机里（VMware / VirtualBox / Hyper-V / QEMU / Parallels / Xen）。
        /// 识别到会按低配处理 —— 见 <see cref="Probe"/> 里的说明。
        /// </summary>
        public static bool IsVirtualMachine { get; private set; }

        /// <summary>识别到的虚拟机名称；物理机为空字符串。</summary>
        public static string VirtualMachineName { get; private set; } = string.Empty;

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

                var vm = DetectVirtualMachine();
                VirtualMachineName = vm ?? string.Empty;
                IsVirtualMachine = vm is not null;

                // 自动化判定：任一条件命中即视为低配。
                // 门槛刻意放得比较宽（4 核 / 6GB），因为启动器自身在 2 核 + 核显的机器上
                // 打开就会明显掉帧，而这类机器在 Win7 用户里很常见。
                //
                // 注意：虚拟机**不算低配**。1.0.9.4 曾把 VM 纳入低配以便强制软件渲染，
                // 但后来查明 VM 里那次崩溃的真实原因是 CET（见 csproj 的 CetCompat），
                // 与渲染无关。既然没有证据表明虚拟 GPU 有问题，就不该凭猜测改渲染路径 ——
                // 有 GPU 直通的 VM 反而会因此白白变慢。VM 信息只写进日志供排查。
                IsLowEndHardware =
                    Is32BitProcess
                    || IsLegacyWindows
                    || TotalMemoryMb > 0 && TotalMemoryMb < 6144
                    || ProcessorCount <= 3;

                Summary = string.Format(
                    "Windows {0}.{1} (Build {2}){3}{4}，{5} 位进程，{6} 核，{7}",
                    major, minor, build,
                    IsWindows11 ? " [Win11]" : string.Empty,
                    IsVirtualMachine ? $" [{VirtualMachineName}]" : string.Empty,
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
        /// 识别虚拟机（VMware / VirtualBox / Hyper-V / QEMU / Parallels / Xen / KVM）。
        ///
        /// <para>
        /// <b>为什么要单独识别</b>：虚拟机里的「显卡」多半是没有 D3D11 的虚拟适配器。
        /// Avalonia 默认渲染顺序是 D3D11 → D3D9 → OpenGL → 软件，每次回退都要重建渲染上下文；
        /// 在某些虚拟 GPU 上这条路径会在 **native 层直接崩掉**，崩溃点在日志系统建立之前，
        /// 于是用户看到的是「双击毫无反应、一个日志文件都没有」，
        /// 事件查看器里是 <c>KERNELBASE.dll</c> + <c>0x80131506</c>（CLR 执行引擎内部错误）。
        /// 与其去赌虚拟 GPU 的驱动质量，不如一开始就走软件渲染 —— 稳定优先。
        /// </para>
        ///
        /// <para>
        /// 判据取注册表 BIOS 段的厂商 / 型号字符串（不需要 WMI，毫秒级）：
        /// 物理机的厂商一般是 Dell/ASUS/Lenovo 等，不会命中这些关键字。
        /// </para>
        /// </summary>
        /// <returns>命中时返回虚拟机名称，物理机返回 null。</returns>
        private static string? DetectVirtualMachine()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;

            try
            {
                using var bios = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");

                var manufacturer = bios?.GetValue("SystemManufacturer") as string ?? string.Empty;
                var product = bios?.GetValue("SystemProductName") as string ?? string.Empty;
                var blob = (manufacturer + " " + product).ToLowerInvariant();

                var byBios = MatchVirtualMachine(blob);
                if (byBios is not null)
                    return byBios;

                // 兜底：有些虚拟机把 BIOS 字符串伪装成物理机，但磁盘枚举仍会暴露厂商
                using var disk = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Disk\Enum");
                var diskEnum = (disk?.GetValue("0") as string ?? string.Empty).ToLowerInvariant();

                return MatchVirtualMachine(diskEnum);
            }
            catch
            {
                // 注册表读不到（策略限制）就当物理机处理，不要因此影响启动
                return null;
            }
        }

        private static string? MatchVirtualMachine(string blob)
        {
            if (string.IsNullOrEmpty(blob))
                return null;

            if (blob.Contains("vmware")) return "VMware";
            if (blob.Contains("virtualbox") || blob.Contains("innotek") || blob.Contains("vbox")) return "VirtualBox";
            if (blob.Contains("hyper-v") || blob.Contains("virtual machine")) return "Hyper-V";
            if (blob.Contains("qemu")) return "QEMU";
            if (blob.Contains("parallels")) return "Parallels";
            if (blob.Contains("xen")) return "Xen";
            if (blob.Contains("bochs")) return "Bochs";
            if (blob.Contains("virtual hd") || blob.Contains("kvm")) return "KVM";

            return null;
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
