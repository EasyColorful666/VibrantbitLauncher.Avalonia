using System;
using System.IO;

namespace VibrantbitLauncher.Services.Download;

/// <summary>
/// 「下到了，但内容不对」——体积不足、sha1 不匹配、服务器返回了错误页面。
///
/// <para>
/// 单独建一个类型的原因：这类失败**不是**磁盘故障也不是网络故障，
/// 但如果不区分，它会被 <see cref="Helpers.DownloadErrors.DescribeFileSystem"/>
/// 当成 <see cref="IOException"/> 报成「磁盘读写失败」，把用户引到「去查磁盘」的死胡同。
/// 界面上应该显示的是「这个文件没下完整，换个源再试」。
/// </para>
/// </summary>
public sealed class DownloadIntegrityException : Exception
{
    public DownloadIntegrityException(string message) : base(message)
    {
    }

    public DownloadIntegrityException(string message, Exception? inner) : base(message, inner)
    {
    }

    /// <summary>拿到的是空文件 / 错误页面。</summary>
    public static DownloadIntegrityException Empty()
        => new("下载到的文件是空的（服务器可能返回了错误页面）");

    /// <summary>体积与清单声明的不一致 —— 镜像端截断的典型症状。</summary>
    public static DownloadIntegrityException SizeMismatch(long expected, long actual)
        => new($"文件大小不符（期望 {expected} 字节，实际 {actual} 字节），可能被服务器截断");

    /// <summary>sha1 校验失败 —— 内容已损坏。</summary>
    public static DownloadIntegrityException HashMismatch()
        => new("文件校验失败（SHA1 不匹配），下载内容已损坏");
}
