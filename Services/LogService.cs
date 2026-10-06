using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VibrantbitLauncher.Models;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 全应用统一的日志入口。
    ///
    /// 之前日志是散着写的：App.xaml.cs 里一份最小 Serilog 配置、启动页又自己往
    /// launch_debug.log 里追加、好几处直接 Debug.WriteLine（Release 下等于没有）。
    /// 出问题时用户拿不到东西可查。这里收拢成一处：
    ///
    ///   · 文件：logs/vibrantbit-YYYYMMDD.log，按天滚动，单文件 10 MB 再滚，按保留天数清理；
    ///   · 内存：环形缓冲最近 1000 条，供设置页里的「最近日志」实时查看；
    ///   · 调试器：Debug.WriteLine，Visual Studio 输出窗口可见（可选，Redmond 默认开）；
    ///   · 等级：运行时可热切换，不用重启；
    ///   · 兜底：AppDomain / TaskScheduler 的未处理异常一律落盘，不再静默吞掉。
    /// </summary>
    public static class LogService
    {
        private const string FileNamePattern = "vibrantbit-.log";
        private const int MemoryCapacity = 1000;
        private const long FileSizeLimitBytes = 10L * 1024 * 1024;

        private static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);
        private static readonly MemoryLogSink Memory = new(MemoryCapacity);
        private static readonly DebugLogSink DebugSink = new();
        private static bool _handlersRegistered;
        private static bool _initialized;

        /// <summary>日志目录（logs/）。</summary>
        public static string LogDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "logs");

        /// <summary>
        /// 当前正在写入的日志文件。按天滚动，所以名字里带日期；
        /// 单文件超限后的分片会带 _001 之类的后缀，但「当前」永远是这个名字。
        /// </summary>
        public static string CurrentLogFile =>
            Path.Combine(LogDirectory, $"vibrantbit-{DateTime.Now:yyyyMMdd}.log");

        /// <summary>可选等级，顺序与界面上一致（由细到粗）。</summary>
        public static IReadOnlyList<LogLevelOption> AvailableLevels { get; } = new[]
        {
            new LogLevelOption("Verbose", "详细 (Verbose)"),
            new LogLevelOption("Debug", "调试 (Debug)"),
            new LogLevelOption("Information", "信息 (Information)"),
            new LogLevelOption("Warning", "警告 (Warning)"),
            new LogLevelOption("Error", "错误 (Error)"),
            new LogLevelOption("Fatal", "致命 (Fatal)"),
        };

        /// <summary>当前生效的最低等级。</summary>
        public static string CurrentLevel => LevelSwitch.MinimumLevel.ToString();

        /// <summary>有新日志写入时触发（可能在任意线程，订阅方需自行切回 UI 线程）。</summary>
        public static event Action<LogEntry>? EntryWritten
        {
            add => Memory.EntryWritten += value;
            remove => Memory.EntryWritten -= value;
        }

        /// <summary>
        /// 初始化日志系统。可重复调用（会先关掉旧 Logger 再重建）。
        /// </summary>
        /// <param name="minimumLevel">最低记录等级，见 <see cref="AvailableLevels"/>。</param>
        /// <param name="retentionDays">日志文件保留天数。</param>
        /// <param name="toDebugOutput">是否同时输出到调试器（Visual Studio 输出窗口）。</param>
        public static void Initialize(string minimumLevel, int retentionDays, bool toDebugOutput)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
            }
            catch
            {
                // 目录建不出来也不能挡住启动
            }

            LevelSwitch.MinimumLevel = ParseLevel(minimumLevel);

            // 先关掉旧实例，否则重复 Initialize 会泄漏文件句柄
            if (_initialized)
                Log.CloseAndFlush();

            var configuration = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(LevelSwitch)
                .Enrich.With(new ThreadIdEnricher())
                .WriteTo.Sink(Memory)
                .WriteTo.File(
                    Path.Combine(LogDirectory, FileNamePattern),
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: FileSizeLimitBytes,
                    retainedFileCountLimit: Math.Max(1, retentionDays),
                    shared: true,
                    outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [T{ThreadId}] {Message:lj}{NewLine}{Exception}");

            if (toDebugOutput)
                configuration = configuration.WriteTo.Sink(DebugSink);

            Log.Logger = configuration.CreateLogger();
            _initialized = true;

            RegisterGlobalExceptionHandlers();
        }

        /// <summary>运行时切换最低记录等级，无需重启。</summary>
        public static void ApplyLevel(string minimumLevel)
        {
            LevelSwitch.MinimumLevel = ParseLevel(minimumLevel);
        }

        /// <summary>按当前配置重新初始化（改保留天数等需要重建 File sink 的参数时用）。</summary>
        public static void Reinitialize()
        {
            var settings = SettingsService.Current;
            Initialize(settings.LogLevel, settings.LogRetentionDays, settings.LogToDebugOutput);
        }

        /// <summary>取当前内存里缓冲的日志快照（时间正序）。</summary>
        public static IReadOnlyList<LogEntry> Snapshot() => Memory.Snapshot();

        /// <summary>清空内存缓冲（不动磁盘文件）。</summary>
        public static void ClearMemory() => Memory.Clear();

        /// <summary>
        /// 清空全部日志：删掉 logs/ 下的日志文件并清空内存缓冲。
        ///
        /// 必须先 CloseAndFlush 释放文件句柄 —— File sink 常驻打开着当天那个文件，
        /// 句柄没放掉时 File.Delete 在 Windows 上一定失败。
        /// </summary>
        /// <returns>实际删除的文件数。</returns>
        public static int ClearAll()
        {
            Log.CloseAndFlush();
            _initialized = false;

            var deleted = 0;
            try
            {
                foreach (var file in Directory.GetFiles(LogDirectory, "vibrantbit-*.log"))
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch
                    {
                        // 单个文件删不掉（被别的进程占用）就跳过
                    }
                }
            }
            catch
            {
                // 目录不可读
            }

            Memory.Clear();
            Reinitialize();

            Log.Information("日志已清空：删除 {Deleted} 个文件", deleted);
            return deleted;
        }

        /// <summary>列出日志文件，按最后写入时间倒序（最新的在最前）。</summary>
        public static IReadOnlyList<FileInfo> GetLogFiles()
        {
            try
            {
                var dir = new DirectoryInfo(LogDirectory);
                if (!dir.Exists)
                    return Array.Empty<FileInfo>();

                return dir.GetFiles("vibrantbit-*.log")
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();
            }
            catch
            {
                return Array.Empty<FileInfo>();
            }
        }

        /// <summary>日志总占用大小（字节）。</summary>
        public static long GetTotalSize()
        {
            try
            {
                return GetLogFiles().Sum(f => f.Length);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>把最新一个日志文件另存到 <paramref name="destination"/>。</summary>
        public static bool ExportLatest(string destination)
        {
            var latest = GetLogFiles().FirstOrDefault();
            if (latest == null)
                return false;

            try
            {
                // 目标就是源文件时 CopyTo 会抛异常，直接当失败处理更清楚
                if (string.Equals(
                        Path.GetFullPath(destination),
                        Path.GetFullPath(latest.FullName),
                        StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("日志导出被跳过：目标与源文件相同（{Path}）", destination);
                    return false;
                }

                latest.CopyTo(destination, overwrite: true);
                Log.Information("日志已导出：{Source} → {Destination}", latest.FullName, destination);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "日志导出失败：{Destination}", destination);
                return false;
            }
        }

        /// <summary>在资源管理器里打开日志目录。</summary>
        public static void OpenLogDirectory()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = LogDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "打开日志目录失败：{Dir}", LogDirectory);
            }
        }

        /// <summary>写一段启动横幅，便于在一份日志里区分每次运行。</summary>
        public static void WriteStartupBanner(string appVersion)
        {
            Log.Information("==================== VibrantbitLauncher {Version} 启动 ====================", appVersion);
            Log.Information(
                "运行环境：{Os} · .NET {Runtime} · {Arch} · {Bit} 位进程",
                RuntimeInformation.OSDescription,
                Environment.Version,
                RuntimeInformation.ProcessArchitecture,
                Environment.Is64BitProcess ? 64 : 32);
            Log.Information("程序目录：{Dir}", AppContext.BaseDirectory);
            Log.Information(
                "日志：{Dir}（等级 {Level}，保留 {Days} 天）",
                LogDirectory,
                LevelSwitch.MinimumLevel,
                SettingsService.Current.LogRetentionDays);
        }

        /// <summary>写一条「最近一次异常」摘要，给设置页的日志卡片做提示用。</summary>
        public static LogEntry? LastError()
        {
            var snapshot = Memory.Snapshot();
            for (var i = snapshot.Count - 1; i >= 0; i--)
            {
                if (snapshot[i].Level >= LogEventLevel.Error)
                    return snapshot[i];
            }

            return null;
        }

        // ===== 内部 =====

        private static LogEventLevel ParseLevel(string? name) =>
            Enum.TryParse<LogEventLevel>(name, ignoreCase: true, out var level)
                ? level
                : LogEventLevel.Information;

        private static void RegisterGlobalExceptionHandlers()
        {
            if (_handlersRegistered)
                return;

            _handlersRegistered = true;

            // 未捕获的异常：进程随后就会挂，所以记 Fatal
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log.Fatal(args.ExceptionObject as Exception, "未处理的应用域异常（进程即将终止）");

            // 被丢弃的 Task 异常：以前完全无声，这里至少留下一条
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Error(args.Exception, "未观察到的任务异常");
                args.SetObserved();
            };
        }

        /// <summary>把当前线程 ID 写进事件属性，供输出模板里的 {ThreadId} 使用。</summary>
        private sealed class ThreadIdEnricher : ILogEventEnricher
        {
            public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
                logEvent.AddOrUpdateProperty(
                    propertyFactory.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
        }

        /// <summary>
        /// 内存环形缓冲 sink：只保留最近 N 条，供应用内查看器显示。
        /// 超出容量时丢最旧的，因此内存占用有上限。
        /// </summary>
        private sealed class MemoryLogSink : ILogEventSink
        {
            private readonly int _capacity;
            private readonly Queue<LogEntry> _entries;

            public MemoryLogSink(int capacity)
            {
                _capacity = capacity;
                _entries = new Queue<LogEntry>(capacity);
            }

            public event Action<LogEntry>? EntryWritten;

            public void Emit(LogEvent logEvent)
            {
                var entry = new LogEntry(
                    logEvent.Timestamp,
                    logEvent.Level,
                    logEvent.RenderMessage(),
                    logEvent.Exception?.ToString());

                lock (_entries)
                {
                    while (_entries.Count >= _capacity)
                        _entries.Dequeue();

                    _entries.Enqueue(entry);
                }

                // 订阅方是 UI，可能抛异常；日志系统本身绝不能被它带崩
                try
                {
                    EntryWritten?.Invoke(entry);
                }
                catch
                {
                    // ignore
                }
            }

            public IReadOnlyList<LogEntry> Snapshot()
            {
                lock (_entries)
                    return _entries.ToList();
            }

            public void Clear()
            {
                lock (_entries)
                    _entries.Clear();
            }
        }

        /// <summary>调试器输出 sink（Visual Studio 输出窗口）。Release 下 Debug.WriteLine 会被编译器去掉，等于空操作。</summary>
        private sealed class DebugLogSink : ILogEventSink
        {
            public void Emit(LogEvent logEvent)
            {
                try
                {
                    // 注意：这里不能写成 $"{logEvent.Level:u3}"。
                    // "{Level:u3}" 是 Serilog 输出模板的写法（由 Serilog 自己解析），
                    // 但 C# 内插字符串会把 :u3 当成枚举的格式说明符交给 LogEventLevel 处理，
                    // 而枚举只认 G/g/X/x/F/f/D/d —— 会直接抛 FormatException。
                    // 所以三字母等级自己算，LogEntry.Shorten 与界面显示共用同一份映射。
                    var line = $"[{logEvent.Timestamp:HH:mm:ss.fff}] [{LogEntry.Shorten(logEvent.Level)}] {logEvent.RenderMessage()}";

                    Debug.WriteLine(line);

                    if (logEvent.Exception != null)
                        Debug.WriteLine(logEvent.Exception.ToString());
                }
                catch
                {
                    // 日志系统本身绝不能把调用方带崩
                }
            }
        }
    }
}
