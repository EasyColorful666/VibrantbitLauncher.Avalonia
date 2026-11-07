using System;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 把游戏目录里的某个子目录"挪到别的盘"，同时让游戏仍然在原位置找得到它。
///
/// <para>
/// Minecraft 只会读 <c>&lt;游戏目录&gt;/resourcepacks</c> 与 <c>saves</c>，
/// 没有提供改路径的参数。所以「换到别的盘」只能用目录链接：
/// 原位置留一个指向新目录的入口，游戏照旧读，实际文件放在容量更大的盘上。
/// </para>
///
/// <para>
/// Windows 上建的是 <b>junction</b> 而不是符号链接 —— 前者不需要管理员权限，
/// 也不需要开启「开发者模式」。其他平台用普通符号链接。
/// </para>
/// </summary>
public static class FolderLinkService
{
    /// <summary>该目录是否已经是一个链接（junction / symlink）。</summary>
    public static bool IsLink(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return false;

            var info = new DirectoryInfo(path);
            return info.LinkTarget is not null
                   || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 让 <paramref name="linkPath"/> 指向 <paramref name="targetPath"/>。
    /// </summary>
    /// <returns>成功返回 null，失败返回给用户看的原因。</returns>
    public static string? Link(string linkPath, string targetPath)
    {
        try
        {
            if (string.Equals(
                    Path.GetFullPath(linkPath).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return "新位置和原位置是同一个目录，不需要移动";
            }

            Directory.CreateDirectory(targetPath);

            if (Directory.Exists(linkPath))
            {
                if (IsLink(linkPath))
                {
                    // 已经是指向别处的链接：先拆掉再建，不然会套一层
                    Directory.Delete(linkPath, recursive: false);
                }
                else if (Directory.EnumerateFileSystemEntries(linkPath).GetEnumerator().MoveNext())
                {
                    // 真实目录且非空：不擅自搬用户的东西
                    return "原位置已有内容。请先把里面的文件移动到新位置，再回来重新选择";
                }
                else
                {
                    Directory.Delete(linkPath, recursive: false);
                }
            }

            CreateLink(linkPath, targetPath);
            Log.Information("目录链接已建立：{Link} → {Target}", linkPath, targetPath);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "建立目录链接失败：{Link} → {Target}", linkPath, targetPath);
            return "建立目录链接失败：" + ex.Message;
        }
    }

    /// <summary>拆掉链接。只删链接本身，目标目录里的文件不受影响。</summary>
    public static string? Unlink(string linkPath)
    {
        try
        {
            if (!Directory.Exists(linkPath) || !IsLink(linkPath))
                return null;

            Directory.Delete(linkPath, recursive: false);
            Directory.CreateDirectory(linkPath);
            Log.Information("目录链接已拆除：{Link}", linkPath);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "拆除目录链接失败：{Link}", linkPath);
            return "拆除目录链接失败：" + ex.Message;
        }
    }

    private static void CreateLink(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // 路径会被塞进一条 cmd 命令串并用双引号包住，所以路径本身不能再带引号 / 换行。
            // 实例目录名可能是从整合包里带进来的（不完全由本机用户决定），
            // 一个 `" & ...` 就能跳出引号去执行别的命令 —— 这里直接拒绝。
            EnsureCmdSafePath(linkPath, "链接路径");
            EnsureCmdSafePath(targetPath, "目标路径");

            // mklink 是 cmd 内建命令，只能通过 cmd 调；/J 建的是 junction，无需提权
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi)
                                ?? throw new InvalidOperationException("无法启动 mklink");

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new IOException(string.IsNullOrWhiteSpace(stderr) ? "mklink 返回失败" : stderr.Trim());

            return;
        }

        Directory.CreateSymbolicLink(linkPath, targetPath);
    }

    /// <summary>
    /// 命令串里的路径只允许「普通文件系统字符」：引号、换行、NUL 都能跳出双引号改变命令语义。
    ///
    /// <para>
    /// 只拦这几种，不去猜 cmd 的元字符（<c>&amp; | ^</c> 在双引号内不生效），
    /// 免得把合法的中文、空格、括号路径也一起挡掉。
    /// </para>
    /// </summary>
    private static void EnsureCmdSafePath(string path, string what)
    {
        if (path.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
            throw new IOException($"{what}含有不被允许的字符（引号或换行）：{path}");
    }
}
