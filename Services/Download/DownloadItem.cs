namespace VibrantbitLauncher.Services.Download;

/// <summary>
/// 一个待下载的文件。
///
/// <para>
/// <b>为什么带 <see cref="ExpectedSize"/> / <see cref="Sha1"/></b>：
/// 有了它们才能在下载前跳过「已经下好的文件」，在下载后识别「下到的是被限流截断的半截文件」。
/// 原版资源里几千个资源对象全部带 sha1 + size，缺了这两个字段就只能靠文件存在性判断，
/// 而「文件在、内容是错的」正是各家镜像最容易制造的问题。
/// </para>
/// </summary>
public sealed record DownloadItem(
    string Url,
    string TargetPath,
    long ExpectedSize = 0,
    string? Sha1 = null,
    /// <summary>该文件在官方源之外的备用地址（镜像专用路径，例如 BMCLAPI 的 /version/&lt;id&gt;/client）。</summary>
    string? AltUrl = null,
    /// <summary>给用户看的短名（默认取文件名）。</summary>
    string? DisplayName = null)
{
    /// <summary>展示名：优先自定义，其次文件名。</summary>
    public string Name => DisplayName ?? Path.GetFileName(TargetPath);
}

/// <summary>批量下载的进度快照。</summary>
public sealed class BatchProgress
{
    /// <summary>0~100。</summary>
    public int Percent { get; init; }

    /// <summary>已接收字节（含正在下载的文件）。</summary>
    public long ReceivedBytes { get; init; }

    /// <summary>预计总字节；有文件大小未知时为 0。</summary>
    public long TotalBytes { get; init; }

    /// <summary>全局速度（字节/秒），所有并行文件的总和。</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>已完成（含跳过）的文件数。</summary>
    public int CompletedFiles { get; init; }

    /// <summary>文件总数。</summary>
    public int TotalFiles { get; init; }

    /// <summary>正在下载的文件名。</summary>
    public string CurrentFile { get; init; } = string.Empty;

    /// <summary>速度文案，例如 <c>12.3 MB/s</c>。</summary>
    public string SpeedText => DownloadSpeedometer.FormatSpeed(BytesPerSecond);

    /// <summary>剩余时间文案，例如 <c>约 1 分 20 秒</c>。</summary>
    public string EtaText =>
        DownloadSpeedometer.FormatEta(Math.Max(0, TotalBytes - ReceivedBytes), BytesPerSecond);
}

/// <summary>批量下载的结果。</summary>
public sealed class BatchResult
{
    /// <summary>真正下载的文件数。</summary>
    public int Downloaded { get; set; }

    /// <summary>已存在而跳过的文件数。</summary>
    public int Skipped { get; set; }

    /// <summary>失败清单（文件路径 + 原因）。</summary>
    public List<(string File, Exception Error)> Failures { get; } = new();

    /// <summary>总字节数（下载 + 跳过）。</summary>
    public long TotalBytes { get; set; }

    /// <summary>是否全部成功。</summary>
    public bool IsSuccess => Failures.Count == 0;
}
