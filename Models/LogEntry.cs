using Avalonia.Media;
using Serilog.Events;

namespace VibrantbitLauncher.Models
{
    /// <summary>
    /// 一条日志记录，供应用内「日志」查看器显示。
    ///
    /// 这里刻意不直接把 Serilog 的 LogEvent 暴露到 UI 层：LogEvent 会持有整条消息的
    /// 属性包，长期留在内存里开销不小；展示只需要时间、等级、正文、异常四样东西。
    /// </summary>
    public sealed class LogEntry
    {
        /// <summary>等级 → 画刷的缓存。颜色取中间调，Light / Dark 两种主题下都能读。</summary>
        private static readonly Dictionary<LogEventLevel, IBrush> Brushes = new()
        {
            [LogEventLevel.Verbose] = Parse("#FF9A9A9A"),
            [LogEventLevel.Debug] = Parse("#FF6E7681"),
            [LogEventLevel.Information] = Parse("#FF2F7DD1"),
            [LogEventLevel.Warning] = Parse("#FFC77700"),
            [LogEventLevel.Error] = Parse("#FFD64545"),
            [LogEventLevel.Fatal] = Parse("#FFC0261F"),
        };

        public LogEntry(DateTimeOffset timestamp, LogEventLevel level, string message, string? exception)
        {
            Timestamp = timestamp;
            Level = level;
            Message = message;
            Exception = exception;

            TimeText = timestamp.LocalDateTime.ToString("HH:mm:ss.fff");
            LevelText = Shorten(level);
            LevelBrush = Brushes.TryGetValue(level, out var brush) ? brush : Brushes[LogEventLevel.Information];
        }

        public DateTimeOffset Timestamp { get; }

        public LogEventLevel Level { get; }

        public string Message { get; }

        /// <summary>异常全文（含堆栈），无异常时为 null。</summary>
        public string? Exception { get; }

        /// <summary>列表左列时间，形如 14:07:31.482。</summary>
        public string TimeText { get; }

        /// <summary>三字母等级，形如 INF / WRN / ERR。</summary>
        public string LevelText { get; }

        /// <summary>等级对应的前景色。</summary>
        public IBrush LevelBrush { get; }

        public bool HasException => !string.IsNullOrEmpty(Exception);

        /// <summary>异常文本，供 XAML 直接绑定（避免在 XAML 里判空）。</summary>
        public string ExceptionText => Exception ?? string.Empty;

        /// <summary>
        /// 等级的三字母缩写（VRB / DBG / INF / WRN / ERR / FTL）。
        /// 界面与调试器输出共用这一份映射，别再各写一套。
        /// </summary>
        public static string Shorten(LogEventLevel level) => level switch
        {
            LogEventLevel.Verbose => "VRB",
            LogEventLevel.Debug => "DBG",
            LogEventLevel.Information => "INF",
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Error => "ERR",
            LogEventLevel.Fatal => "FTL",
            _ => "???"
        };

        private static IBrush Parse(string hex)
        {
            var color = Color.TryParse(hex, out var parsed) ? parsed : Colors.Gray;
            return new SolidColorBrush(color);
        }
    }

    /// <summary>
    /// 日志等级下拉项。<see cref="Key"/> 是写进 settings.json 的值，
    /// <see cref="Display"/> 是界面上给人看的名字。
    /// </summary>
    public sealed class LogLevelOption
    {
        public LogLevelOption(string key, string display)
        {
            Key = key;
            Display = display;
        }

        public string Key { get; }

        public string Display { get; }

        public override string ToString() => Display;
    }
}
