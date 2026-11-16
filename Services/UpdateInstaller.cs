using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VibrantbitLauncher.Services;

/// <summary>
/// 把下载好的更新包装回去：解压到临时目录 → 起一个更新脚本 → 退出自己。
///
/// <para>
/// <b>为什么不能在本进程里直接覆盖</b>：Windows 上正在运行的 exe 被系统锁着，
/// 覆盖会报「文件被占用」；Linux / macOS 虽然允许覆盖（inode 换了），
/// 但程序目录里其它文件同样可能被占用。所以替换动作必须发生在「本进程已退出」之后，
/// 由一个独立的脚本完成：它等 PID 消失 → 复制 → 重新拉起新版本 → 自删。
/// </para>
///
/// <para>
/// <b>脚本里的路径是拼进去的</b>，所以路径必须过一遍字符白名单：
/// 带引号 / 百分号 / 感叹号 / 换行的路径会让 cmd 解析出完全不同的命令，
/// 等于把「写一个路径」变成「执行任意命令」。
/// </para>
/// </summary>
public static class UpdateInstaller
{
    /// <summary>等旧进程退出的上限（秒）。超时就照常往下走，让复制自己失败并回滚重启。</summary>
    private const int WaitSeconds = 120;

    /// <summary>当前正在运行的程序本体。开发环境（dotnet 跑起来）时它不是启动器，不能拿去替换。</summary>
    public static string? ResolveLauncherPath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            return null;

        var name = Path.GetFileNameWithoutExtension(processPath);
        if (!name.Contains("VibrantbitLauncher", StringComparison.OrdinalIgnoreCase))
        {
            // dotnet.exe / testhost.exe 之类。真按这个路径去替换，装完这台机器就没运行时了。
            Log.Warning("当前进程不是启动器本体（{Path}），拒绝自动更新", processPath);
            return null;
        }

        return processPath;
    }

    /// <summary>
    /// 解压 + 起更新脚本。返回 <c>null</c> 表示脚本已经拉起来，调用方应当立刻退出程序；
    /// 返回字符串表示没法继续，界面直接显示这句话。
    /// </summary>
    public static async Task<string?> InstallAsync(
        string packagePath,
        string version,
        CancellationToken cancellationToken = default)
    {
        var launcher = ResolveLauncherPath();
        if (launcher is null)
            return "当前不是以启动器本体运行（例如开发环境），无法自动替换，请手动解压安装包";

        var targetDir = Path.GetDirectoryName(launcher);
        if (string.IsNullOrEmpty(targetDir))
            return "无法定位程序所在目录";

        string? staging = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            staging = Path.Combine(
                Path.GetTempPath(),
                $"vbl-update-{Sanitize(version)}-{Guid.NewGuid():N}");

            Directory.CreateDirectory(staging);

            await ExtractAsync(packagePath, staging, cancellationToken).ConfigureAwait(false);

            // 包里必须有一个与当前程序同名的可执行文件，否则装完就是「启动器不见了」。
            var expected = Path.GetFileName(launcher);
            var extracted = Directory
                .EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), expected, StringComparison.OrdinalIgnoreCase));

            if (extracted is null)
                return $"安装包里没有找到 {expected}，更新已中止";

            var script = OperatingSystem.IsWindows()
                ? WriteWindowsScript(targetDir, staging, launcher, Environment.ProcessId, packagePath)
                : WriteUnixScript(targetDir, staging, launcher, Environment.ProcessId, packagePath);

            StartScript(script);

            Log.Information("更新脚本已启动：{Script}（目标 {Target}）", script, targetDir);
            return null;
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(staging);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "准备更新失败（包 {Package}）", packagePath);
            TryDeleteDirectory(staging);
            return "解压安装包失败：" + ex.Message;
        }
    }

    // ==================== 解压 ====================

    /// <summary>
    /// 按扩展名解压。zip 给 Windows，tar.gz 给 Linux / macOS ——
    /// 发布脚本就是这么打的包，认错格式会直接抛「找不到中央目录」。
    /// </summary>
    private static async Task ExtractAsync(string packagePath, string staging, CancellationToken ct)
    {
        if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(packagePath);

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();

                // 目录项：FullName 以 / 结尾、Name 为空
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var dest = ResolveInside(staging, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }

            return;
        }

        await using var file = File.OpenRead(packagePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        while (await reader.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            var relative = entry.Name.Replace('\\', '/').TrimStart('.', '/');
            if (relative.Length == 0)
                continue;

            var dest = ResolveInside(staging, relative);

            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(dest);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);

            // tar 里带着权限位，解出来的二进制必须补上可执行位，否则 Linux 上「装完打不开」
            if (!OperatingSystem.IsWindows() && entry.Mode != UnixFileMode.None)
            {
                try { File.SetUnixFileMode(dest, entry.Mode); }
                catch (Exception ex) { Log.Debug(ex, "设置权限失败：{Path}", dest); }
            }
        }
    }

    /// <summary>
    /// 把包内路径拼到解压根下，同时挡住 <c>../</c> 逃逸。
    /// 包是从自己发布的地址下来的、还过了 sha256，但「多一道检查」比「相信上游」便宜。
    /// </summary>
    private static string ResolveInside(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('\\', '/')));

        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"安装包里有越界路径：{relative}");

        return full;
    }

    // ==================== 更新脚本 ====================

    /// <summary>cmd 里会改变语义的字符：引号、变量展开、管道、重定向、延迟展开、换行。</summary>
    private static readonly char[] CmdUnsafe = { '"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n' };

    /// <summary>sh 里会改变语义的字符：引号、变量展开、命令替换、转义、换行。</summary>
    private static readonly char[] ShUnsafe = { '"', '$', '`', '\\', '\r', '\n' };

    internal static string WriteWindowsScript(
        string targetDir,
        string sourceDir,
        string exePath,
        int pid,
        string packagePath)
    {
        foreach (var path in new[] { targetDir, sourceDir, exePath, packagePath })
        {
            if (path.IndexOfAny(CmdUnsafe) >= 0)
                throw new InvalidOperationException($"路径里含有不能在命令行里安全表示字符：{path}");
        }

        // ⚠️ 这个脚本必须是**纯 ASCII**。
        // 批处理文件由 cmd 按控制台的 OEM 代码页（中文系统是 936）解码，
        // 而这里写出去的是 UTF-8 —— 中文注释会被解成乱码，行尾还可能吞掉换行，
        // 后面的 `start` 就变成「不是内部或外部命令」（实测返回 9009，更新后不重启）。
        // 日志里的文字只是给排查用的，英文足够。
        var script = $"""
            @echo off
            setlocal enabledelayedexpansion
            set "TARGET={targetDir}"
            set "SRC={sourceDir}"
            set "EXE={exePath}"
            set "PKG={packagePath}"
            set "LOG=%TEMP%\vbl-update.log"

            echo ==== %date% %time% update start (waiting for pid {pid}) ==== >> "%LOG%"

            rem wait until the old process is really gone, otherwise the exe is still locked
            set /a tries=0
            :wait
            tasklist /FI "PID eq {pid}" /NH 2>nul | find " {pid} " >nul
            if errorlevel 1 goto copy
            set /a tries+=1
            if !tries! GEQ {WaitSeconds} goto copy
            timeout /t 1 /nobreak >nul
            goto wait

            :copy
            echo old process gone, copying after !tries!s >> "%LOG%"

            rem overwrite only, never delete: settings.json, logs and .minecraft live here
            robocopy "%SRC%" "%TARGET%" /E /COPY:DAT /R:3 /W:1 /NFL /NDL /NJH /NJS >nul
            set "rc=!errorlevel!"
            echo robocopy rc=!rc! >> "%LOG%"
            if !rc! GEQ 8 goto fail

            if exist "%PKG%" del /f /q "%PKG%" >nul 2>nul
            rd /s /q "%SRC%" >nul 2>nul

            rem pushd sets the working directory; the script itself runs in a temp folder
            pushd "%TARGET%"
            start "" "%EXE%"
            set "startrc=!errorlevel!"
            popd
            echo start rc=!startrc! >> "%LOG%"

            rem 9009 / 1 means cmd could not spawn it; explorer.exe is a second chance
            if not "!startrc!"=="0" explorer.exe "%EXE%"
            goto bye

            :fail
            echo copy failed, restarting the old build >> "%LOG%"
            pushd "%TARGET%"
            start "" "%EXE%"
            popd
            rd /s /q "%SRC%" >nul 2>nul

            :bye
            rem delete this script itself (goto first so cmd releases the file handle)
            (goto) 2>nul & del "%~f0"
            """;

        return WriteScript(".cmd", script);
    }

    internal static string WriteUnixScript(
        string targetDir,
        string sourceDir,
        string exePath,
        int pid,
        string packagePath)
    {
        foreach (var path in new[] { targetDir, sourceDir, exePath, packagePath })
        {
            if (path.IndexOfAny(ShUnsafe) >= 0)
                throw new InvalidOperationException($"路径里含有不能在 shell 里安全表示的字符：{path}");
        }

        var script = $"""
            #!/bin/sh
            TARGET="{targetDir}"
            SRC="{sourceDir}"
            EXE="{exePath}"
            PKG="{packagePath}"

            i=0
            while kill -0 {pid} 2>/dev/null; do
              i=$((i + 1))
              [ "$i" -ge {WaitSeconds} ] && break
              sleep 1
            done

            if cp -R "$SRC"/. "$TARGET"/; then
              chmod +x "$EXE" 2>/dev/null
              rm -f "$PKG"
              rm -rf "$SRC"
              ("$EXE" >/dev/null 2>&1 &)
            else
              # even when copying fails, bring the old build back up
              ("$EXE" >/dev/null 2>&1 &)
              rm -rf "$SRC"
            fi

            rm -f "$0"
            """;

        return WriteScript(".sh", script);
    }

    private static string WriteScript(string extension, string content)
    {
        var folder = Path.Combine(Path.GetTempPath(), "vbl-updater");
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, $"update-{Guid.NewGuid():N}{extension}");

        // cmd 对 LF 换行的批处理不稳（goto 标签偶尔跳不过去），Windows 上必须写 CRLF；
        // sh 反过来，CRLF 会让 shebang / 行尾多一个 \r 而报「bad interpreter」。
        var text = content.Replace("\r\n", "\n");
        if (OperatingSystem.IsWindows())
            text = text.Replace("\n", "\r\n");

        File.WriteAllText(path, text);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return path;
    }

    private static void StartScript(string scriptPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!
            });
            return;
        }

        Process.Start(new ProcessStartInfo("/bin/sh", $"\"{scriptPath}\"")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!
        });
    }

    // ==================== 杂项 ====================

    /// <summary>版本号进文件名前先洗一遍，免得 1.0.34.7+build 之类把路径搞歪。</summary>
    private static string Sanitize(string text)
    {
        var allowed = text.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray();
        var result = new string(allowed);
        return result.Length == 0 ? "latest" : result;
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "清理临时解压目录失败：{Path}", path);
        }
    }
}
