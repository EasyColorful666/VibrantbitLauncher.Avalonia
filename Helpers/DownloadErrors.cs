using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 下载过程中可能冒出来的**本地文件系统异常**的分类与翻译。
///
/// 为什么单独做一层：网络错误（超时 / 404 / 限流）已经在各自的下载器里翻成了中文，
/// 但「写文件」这一步的异常一直是裸的 —— 用户看到的就是
/// <c>The process cannot access the file '... .vbltmp' because it is being used by another process.</c>
/// 这种纯英文原文，既看不懂也不知道下一步做什么（2026-10-10 用户反馈的正是这一条）。
///
/// 这里按异常类型 / Win32 HRESULT / 关键字把常见几类认出来，
/// 分别给出「原因 + 怎么办」的中文说明，并把「重试也没用」的几类单独标出来，
/// 免得磁盘满了还白试 10 轮、让用户干等。
///
/// 所有判断都会**沿 InnerException 链查找**：真实调用里异常常被包一层
/// （<c>AggregateException</c>、<c>HttpRequestException</c> 的内层 IOException 等），
/// 只看最外层会漏判。
/// </summary>
public static class DownloadErrors
{
    // —— Win32 错误码对应的 HRESULT（.NET 的 IOException.HResult 用的就是这个编码）——
    private const int ErrorSharingViolation = unchecked((int)0x80070020); // 32  文件被占用
    private const int ErrorLockViolation = unchecked((int)0x80070021);    // 33  文件区域被锁定
    private const int ErrorAccessDenied = unchecked((int)0x80070005);     // 5   拒绝访问
    private const int ErrorDiskFull = unchecked((int)0x80070070);         // 112 磁盘已满
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);   // 39  磁盘已满
    private const int ErrorFilenameExcedRange = unchecked((int)0x800700CE); // 206 路径过长

    /// <summary>沿 InnerException 向外展开（深度上限 8，防畸形链造成死循环）。</summary>
    private static IEnumerable<Exception> Chain(Exception? ex)
    {
        var depth = 0;
        for (var e = ex; e is not null && depth < 8; e = e.InnerException, depth++)
            yield return e;
    }

    /// <summary>
    /// 异常是不是「目标文件正被别的句柄占用」。
    ///
    /// 这是本机最常见的一类：游戏还开着（jar 已被 JVM 加载）、杀毒软件正在扫描刚落盘的文件、
    /// 或者同一个文件的两次并行下载撞在一起。Windows 报的就是
    /// <c>IOException</c> + <c>ERROR_SHARING_VIOLATION</c>。
    /// </summary>
    public static bool IsFileInUse(Exception? ex)
        => Chain(ex).Any(e =>
            (e is IOException io &&
             (io.HResult == ErrorSharingViolation || io.HResult == ErrorLockViolation))
            || e.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("正由另一进程使用", StringComparison.OrdinalIgnoreCase));

    /// <summary>磁盘（或分区）已满。</summary>
    public static bool IsDiskFull(Exception? ex)
        => Chain(ex).Any(e => e.HResult == ErrorDiskFull || e.HResult == ErrorHandleDiskFull);

    /// <summary>权限不足：目标是只读目录，或被杀毒软件的「受控文件夹访问」拦了。</summary>
    public static bool IsAccessDenied(Exception? ex)
        => Chain(ex).Any(e => e is UnauthorizedAccessException || e.HResult == ErrorAccessDenied);

    /// <summary>路径过长（超过 Windows 的 260 字符上限）。</summary>
    public static bool IsPathTooLong(Exception? ex)
        => Chain(ex).Any(e => e is PathTooLongException || e.HResult == ErrorFilenameExcedRange);

    /// <summary>
    /// 异常其实是**网络**问题，而不是本地文件系统问题。
    ///
    /// <para>
    /// 必须单独判出来的原因：<c>HttpClient</c> 读响应体时，底层的 <c>SocketException</c> /
    /// TLS 中断会被包成一个 <c>IOException</c>（消息形如
    /// <c>Unable to read data from the transport connection: …</c>），
    /// 而 <see cref="DescribeFileSystem"/> 的兜底分支会把**任何** <c>IOException</c> 当成
    /// 「磁盘读写失败」——于是网络断流会被报成磁盘故障，把用户往「检查磁盘」的错误方向引
    /// （本机端到端测试实测到：maven 下载中途断流，提示成「磁盘读写失败」）。
    /// </para>
    ///
    /// <para>判据：链上出现 <c>SocketException</c>，或消息里带传输层关键字。</para>
    /// </summary>
    public static bool LooksLikeNetwork(Exception? ex)
        => Chain(ex).Any(e =>
            e is System.Net.Sockets.SocketException
            || e.Message.Contains("transport connection", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("connection was closed", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("connection was reset", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("远程主机强迫关闭", StringComparison.Ordinal)
            || e.Message.Contains("网络", StringComparison.Ordinal));

    /// <summary>
    /// 「重试也没有意义」的一类 —— 磁盘满、没权限、路径过长。
    /// 碰到这些要立刻放弃，而不是把「重试次数」白跑完。
    /// </summary>
    public static bool IsFatal(Exception? ex)
        => IsDiskFull(ex) || IsAccessDenied(ex) || IsPathTooLong(ex);

    /// <summary>
    /// 把（可能被包了一层的）文件系统异常翻成中文说明；
    /// 不是文件系统错误时返回 <c>null</c>，交给调用方继续按网络错误处理。
    /// </summary>
    public static string? DescribeFileSystem(Exception? ex)
    {
        if (ex is null)
            return null;

        // 顺序有讲究：越「具体、越有指向性」的原因越先判。
        if (IsFileInUse(ex))
        {
            return "文件正被其他程序占用（常见原因：游戏还在运行、杀毒软件正在扫描该文件，"
                + "或同时开了两个启动器）。请关闭后重试。";
        }

        if (IsPathTooLong(ex))
            return "文件路径过长（超过 Windows 上限）。请把游戏目录移到更浅的位置（例如 D:\\MC）后重试。";

        if (IsDiskFull(ex))
            return "磁盘空间不足，请清理磁盘后重试。";

        if (Chain(ex).Any(e => e is DirectoryNotFoundException))
            return "目标目录不存在或已被删除，请检查游戏目录设置。";

        if (IsAccessDenied(ex))
            return "没有写入权限（目标目录可能是只读，或被杀毒软件拦截）。请更换目录，或以管理员身份运行后重试。";

        // 网络类 IOException（socket 断流被 HttpClient 包成 IOException）**不能**当磁盘错误报，
        // 返回 null 让调用方按「网络错误」处理。
        if (LooksLikeNetwork(ex))
            return null;

        var io = Chain(ex).OfType<IOException>().FirstOrDefault();
        return io is not null ? "磁盘读写失败：" + io.Message : null;
    }
}
