using System.Diagnostics;

namespace VibrantbitLauncher.Services.Download;

/// <summary>
/// 全局下载速度表：所有正在下载的文件都往这里报字节数，
/// 界面要的「当前速度」永远是一个**总量**，而不是某个文件的局部速度。
///
/// <para>
/// 为什么不做成「每个批次各自统计」：一个安装流程里同时可能在跑
/// 原版依赖 + 加载器库 + 整合包模组（并行），三处各自算速度只会得到三个互相矛盾的数。
/// 这里用累计字节差分法算总速度，采样窗口 500ms，再做一次指数平滑 ——
/// 分块下载时单块进度的抖动很大，不平滑的话数字会跳得看不清。
/// </para>
///
/// <para>线程安全：<see cref="Report"/> 会被几十个下载分块并发调用，只用 <see cref="Interlocked"/>。</para>
/// </summary>
public static class DownloadSpeedometer
{
    /// <summary>采样间隔（毫秒）。太密数字乱跳，太疏看不清趋势。</summary>
    private const int SampleIntervalMs = 500;

    /// <summary>连续这么多个采样窗口没收到字节，就认为已经停了，速度归零。</summary>
    private const int IdleSamplesForZero = 2;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>累计收到的字节数（单调递增）。</summary>
    private static long _totalBytes;

    private static long _lastSampleBytes;
    private static long _lastSampleTicks = Clock.ElapsedTicks;
    private static int _idleSamples;

    /// <summary>平滑后的速度（字节/秒）。用 long 存，读取时不用锁。</summary>
    private static long _smoothedBytesPerSecond;

    private static Timer? _timer;

    static DownloadSpeedometer()
    {
        // 惰性启动：只有第一次真正下载时才会建这个定时器
        _timer = new Timer(_ => Sample(), null, SampleIntervalMs, SampleIntervalMs);
    }

    /// <summary>上报「又收到了这么多字节」。必须是增量，不能是累计值。</summary>
    public static void Report(long deltaBytes)
    {
        if (deltaBytes <= 0)
            return;

        Interlocked.Add(ref _totalBytes, deltaBytes);
    }

    /// <summary>当前总速度（字节/秒）。</summary>
    public static double CurrentBytesPerSecond =>
        Interlocked.Read(ref _smoothedBytesPerSecond);

    /// <summary>本次会话累计下载了多少字节。</summary>
    public static long TotalBytes => Interlocked.Read(ref _totalBytes);

    /// <summary>把总速度格式化成界面文案，例如 <c>12.3 MB/s</c>。太慢或没在下载时给空串。</summary>
    public static string FormatSpeed()
    {
        var speed = CurrentBytesPerSecond;
        return speed < 1024 ? string.Empty : FormatSpeed(speed);
    }

    /// <summary>格式化一个指定的速度值。</summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
            return string.Empty;

        if (bytesPerSecond < 1024)
            return $"{bytesPerSecond:F0} B/s";

        if (bytesPerSecond < 1024 * 1024)
            return $"{bytesPerSecond / 1024:F0} KB/s";

        return $"{bytesPerSecond / 1024 / 1024:F1} MB/s";
    }

    /// <summary>把字节数格式化成可读体积，例如 <c>248.6 MB</c>。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        if (bytes < 1024)
            return $"{bytes} B";

        if (bytes < 1024L * 1024)
            return $"{bytes / 1024d:F0} KB";

        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / 1024d / 1024:F1} MB";

        return $"{bytes / 1024d / 1024 / 1024:F2} GB";
    }

    /// <summary>
    /// 按当前速度估算剩余时间，例如 <c>约 1 分 20 秒</c>。
    /// 速度太低或剩余量未知时给空串 —— 宁可不说，也不要显示出「剩余 999 分钟」。
    /// </summary>
    public static string FormatEta(long remainingBytes, double bytesPerSecond)
    {
        if (remainingBytes <= 0 || bytesPerSecond < 8 * 1024)
            return string.Empty;

        var seconds = remainingBytes / bytesPerSecond;
        if (seconds is > 0 and < 1)
            return "不到 1 秒";

        if (seconds >= 3600 * 3)
            return string.Empty;

        var total = (int)Math.Round(seconds);
        return total < 60
            ? $"约 {total} 秒"
            : $"约 {total / 60} 分 {total % 60} 秒";
    }

    /// <summary>采样一次：用两次采样之间的字节差算出瞬时速度，再做指数平滑。</summary>
    private static void Sample()
    {
        var nowTicks = Clock.ElapsedTicks;
        var elapsedSeconds = (nowTicks - _lastSampleTicks) / (double)Stopwatch.Frequency;
        var lastBytes = Interlocked.Exchange(ref _lastSampleBytes, Interlocked.Read(ref _totalBytes));
        var delta = Interlocked.Read(ref _totalBytes) - lastBytes;
        _lastSampleTicks = nowTicks;

        if (elapsedSeconds <= 0)
            return;

        var instantaneous = delta / elapsedSeconds;

        if (delta == 0)
        {
            // 连续几个窗口都没数据 → 真的停了（不是慢），直接归零，
            // 否则界面上会长期挂着「0.3 MB/s」这种假速度。
            if (++_idleSamples >= IdleSamplesForZero)
            {
                Interlocked.Exchange(ref _smoothedBytesPerSecond, 0);
                return;
            }
        }
        else
        {
            _idleSamples = 0;
        }

        var previous = Interlocked.Read(ref _smoothedBytesPerSecond);
        // 新值权重 0.4：既跟得上速度变化，又不会因为某个分块收尾而骤然归零
        var smoothed = previous <= 0 ? instantaneous : previous * 0.6 + instantaneous * 0.4;
        Interlocked.Exchange(ref _smoothedBytesPerSecond, (long)Math.Max(0, smoothed));
    }
}
