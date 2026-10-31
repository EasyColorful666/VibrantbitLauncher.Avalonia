using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services;

/// <summary>一次存档备份。界面上的一行就是它。</summary>
public sealed class SaveBackupEntry
{
    /// <summary>zip 的完整路径。</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>属于哪个实例。</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>打包时刻。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>zip 体积（字节）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>为什么打这一份：<c>auto</c> 启动前自动 / <c>manual</c> 手动 / <c>prerestore</c> 恢复前留档。</summary>
    public string Reason { get; init; } = "manual";

    /// <summary>包里的存档名（顶层目录名）。</summary>
    public IReadOnlyList<string> Worlds { get; init; } = Array.Empty<string>();

    public string DisplayTime => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

    public string DisplaySize => SizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{SizeBytes / 1024d / 1024d / 1024d:0.0} GB",
        >= 1024L * 1024 => $"{SizeBytes / 1024d / 1024d:0.0} MB",
        >= 1024 => $"{SizeBytes / 1024d:0} KB",
        _ => $"{SizeBytes} B"
    };

    public string ReasonLabel => Reason switch
    {
        "auto" => "启动前自动",
        "prerestore" => "恢复前留档",
        _ => "手动"
    };

    /// <summary>「村庄、矿洞 等 3 个」这种一行摘要，最多列三个名字。</summary>
    public string DisplayWorlds
    {
        get
        {
            if (Worlds.Count == 0)
                return "不含存档";

            var head = string.Join("、", Worlds.Take(3));
            return Worlds.Count > 3 ? $"{head} 等 {Worlds.Count} 个" : head;
        }
    }

    public string DisplayLine => $"{DisplayTime} · {DisplaySize} · {ReasonLabel} · {DisplayWorlds}";
}

/// <summary>
/// 存档备份：把某个实例的 <c>saves</c> 目录整份打包成 zip，可列表、可恢复、可自动清理。
///
/// <para>
/// <b>为什么存 zip 而不是复制目录</b>：一份存档动辄几百 MB、上万个文件，
/// 复制一遍又慢又占地方；zip 顺带做了压缩，一个实例的历史能压到几份的空间里。
/// 备份只打包一次、之后只读，所以用最快档的压缩换速度。
/// </para>
///
/// <para>
/// <b>备份放在哪</b>：<c>&lt;.minecraft&gt;/VBL/backups/saves/&lt;实例&gt;/</c>。
/// 刻意不放进 <c>saves</c> 里 —— 游戏会把 saves 下的每个目录都当成一个存档列出来，
/// 备份要是塞在那里，单人游戏列表里会冒出一堆「backup」。
/// </para>
///
/// <para>
/// <b>打包时跳过什么</b>：<c>session.lock</c>（游戏运行时被独占，读不了也没意义）、
/// 重解析点（本项目会把 saves 建成目录链接指到别的盘，跟进去可能跨盘甚至成环）。
/// </para>
/// </summary>
public static class SaveBackupService
{
    /// <summary>文件名里的时间戳格式。同一秒内打两次会补序号，不会互相覆盖。</summary>
    private const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>备份根目录：<c>.minecraft/VBL/backups/saves</c>。</summary>
    public static string BackupRoot =>
        Path.Combine(GameInstanceHelper.GetMinecraftFolder(), "VBL", "backups", "saves");

    /// <summary>某个实例的备份目录。</summary>
    public static string FolderOf(string instanceId) => Path.Combine(BackupRoot, Safe(instanceId));

    /// <summary>某个实例真正的 saves 目录（跟随「版本隔离」）。</summary>
    public static string SavesOf(string instanceId) => GameInstanceHelper.ResolveInstanceFolder(instanceId, "saves");

    /// <summary>把实例 id 变成安全的目录名。</summary>
    private static string Safe(string id)
    {
        var cleaned = new string(id
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .ToArray())
            .Trim();

        return string.IsNullOrEmpty(cleaned) ? "default" : cleaned;
    }

    // ============================ 读 ============================

    /// <summary>saves 下现有的存档目录名（按名字排序）。目录不存在时返回空表。</summary>
    public static IReadOnlyList<string> ListWorlds(string instanceId)
    {
        try
        {
            var saves = SavesOf(instanceId);
            if (!Directory.Exists(saves))
                return Array.Empty<string>();

            return Directory.EnumerateDirectories(saves)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取存档列表失败：{Instance}", instanceId);
            return Array.Empty<string>();
        }
    }

    /// <summary>某个实例已有全部备份，新的在前。</summary>
    public static IReadOnlyList<SaveBackupEntry> List(string instanceId)
    {
        var folder = FolderOf(instanceId);
        if (!Directory.Exists(folder))
            return Array.Empty<SaveBackupEntry>();

        var result = new List<SaveBackupEntry>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.zip"))
            {
                var info = new FileInfo(file);

                // 时间以文件名里的时间戳为准：复制 / 移动备份文件不该改动它的「打包时刻」
                var createdAt = ParseStamp(file) ?? info.CreationTime;
                var meta = ReadMeta(file);

                result.Add(new SaveBackupEntry
                {
                    FilePath = file,
                    InstanceId = instanceId,
                    CreatedAt = createdAt,
                    SizeBytes = info.Length,
                    Reason = meta?.Reason ?? GuessReason(file),
                    Worlds = meta?.Worlds ?? (IReadOnlyList<string>)Array.Empty<string>()
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "列出存档备份失败：{Instance}", instanceId);
        }

        return result.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.FilePath).ToList();
    }

    private static DateTime? ParseStamp(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var underscore = name.IndexOf('_');
        var stamp = underscore > 0 ? name[..underscore] : name;

        return DateTime.TryParseExact(stamp, StampFormat, null, System.Globalization.DateTimeStyles.None, out var time)
            ? time
            : null;
    }

    private static string GuessReason(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var underscore = name.IndexOf('_');
        return underscore >= 0 && underscore + 1 < name.Length ? name[(underscore + 1)..] : "manual";
    }

    // ============================ 打包 ============================

    /// <summary>
    /// 给一个实例打一份备份。
    ///
    /// <returns>打好了返回条目；没有任何存档时返回 null（不产生空包）。</returns>
    /// </summary>
    public static Task<SaveBackupEntry?> CreateAsync(
        string instanceId,
        string reason = "manual",
        IProgress<string>? status = null,
        CancellationToken ct = default)
        => Task.Run(() => CreateCore(instanceId, reason, status, ct), ct);

    private static SaveBackupEntry? CreateCore(
        string instanceId,
        string reason,
        IProgress<string>? status,
        CancellationToken ct)
    {
        var saves = SavesOf(instanceId);
        if (!Directory.Exists(saves))
            return null;

        var worlds = Directory.EnumerateDirectories(saves)
            .Where(dir => !IsReparsePoint(dir))
            .ToList();

        if (worlds.Count == 0)
            return null;

        status?.Report($"正在备份 {worlds.Count} 个存档…");

        var folder = FolderOf(instanceId);
        Directory.CreateDirectory(folder);

        var now = DateTime.Now;
        var baseName = $"{now.ToString(StampFormat)}_{reason}";
        var target = Path.Combine(folder, baseName + ".zip");

        // 同一秒内再打一次（连点两下按钮）不能覆盖上一份
        for (var i = 2; File.Exists(target); i++)
            target = Path.Combine(folder, $"{baseName}-{i}.zip");

        var names = new List<string>();

        using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var world in worlds)
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileName(world);
                names.Add(name);

                AddDirectory(zip, world, name, ct, null);
            }
        }

        var entry = new SaveBackupEntry
        {
            FilePath = target,
            InstanceId = instanceId,
            CreatedAt = now,
            SizeBytes = new FileInfo(target).Length,
            Reason = reason,
            Worlds = names
        };

        WriteMeta(target, entry);

        Log.Information("存档已备份：{Instance} → {File}（{Worlds} 个存档，{Size}）",
            instanceId, Path.GetFileName(target), names.Count, entry.DisplaySize);

        status?.Report($"已备份 {names.Count} 个存档（{entry.DisplaySize}）");
        return entry;
    }

    /// <summary>递归把一个目录塞进 zip。<paramref name="entryRoot"/> 是包内路径前缀。</summary>
    private static void AddDirectory(ZipArchive zip, string dir, string entryRoot, CancellationToken ct, Action<string>? skip)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file);

            // 游戏运行时独占这个文件，读不了；它也不含任何玩家数据
            if (string.Equals(fileName, "session.lock", StringComparison.OrdinalIgnoreCase))
                continue;

            var entryName = entryRoot + "/" + fileName;

            try
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                using var target = entry.Open();
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                source.CopyTo(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件读不了不该让整次备份失败（游戏占用的 .lock、权限怪异的文件）
                skip?.Invoke(entryName);
                Log.Debug(ex, "备份时跳过文件：{File}", file);
            }
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            ct.ThrowIfCancellationRequested();

            // 存档里几乎不会有目录链接，但地域文件夹被玩家自己改过链接的情况存在过
            if (IsReparsePoint(sub))
                continue;

            AddDirectory(zip, sub, entryRoot + "/" + Path.GetFileName(sub), ct, skip);
        }
    }

    private static bool IsReparsePoint(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    // ============================ 恢复 ============================

    /// <summary>
    /// 用一份备份覆盖当前存档。
    ///
    /// <para>
    /// 动手之前**先把现在的存档再备份一份**（reason=prerestore）：
    /// 恢复本身就是一次破坏性操作，而「想恢复到昨天」和「刚才点错了」之间没有防火墙。
    /// </para>
    /// </summary>
    public static Task<string> RestoreAsync(
        SaveBackupEntry entry,
        IProgress<string>? status = null,
        CancellationToken ct = default)
        => Task.Run(() => RestoreCore(entry, status, ct), ct);

    private static string RestoreCore(SaveBackupEntry entry, IProgress<string>? status, CancellationToken ct)
    {
        if (!File.Exists(entry.FilePath))
            return "备份文件已经不在那个位置了";

        // 先验一遍 zip 能不能打开，再动现有存档
        try
        {
            using var probe = ZipFile.OpenRead(entry.FilePath);
            if (probe.Entries.Count == 0)
                return "这个备份是空的，没有恢复";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "备份文件无法打开：{File}", entry.FilePath);
            return "备份文件已损坏，无法恢复";
        }

        var instanceId = entry.InstanceId;
        var saves = SavesOf(instanceId);
        Directory.CreateDirectory(saves);

        // 兜底：先把「当前」留档，失败也继续（可能本来就没有存档）
        try
        {
            status?.Report("先把当前存档留一份…");
            CreateCore(instanceId, "prerestore", null, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "恢复前的留档失败，继续恢复");
        }

        status?.Report("正在还原文件…");

        var staging = Path.Combine(FolderOf(instanceId), ".restore-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(entry.FilePath, staging, overwriteFiles: true);

            var restored = 0;

            foreach (var dir in Directory.EnumerateDirectories(staging))
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileName(dir);
                var target = Path.Combine(saves, name);

                // 同名存档先清掉：只覆盖文件会让「备份里没有的地域」残留在原地，
                // 恢复出来的世界和备份时不是同一个状态
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);

                Directory.Move(dir, target);
                restored++;
            }

            Log.Information("存档已恢复：{Instance} ← {File}（{Worlds} 个）",
                instanceId, Path.GetFileName(entry.FilePath), restored);

            return restored == 0 ? "这个备份里没有存档目录" : $"已恢复 {restored} 个存档";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "恢复存档失败：{File}", entry.FilePath);
            return "恢复失败：" + FriendlyError.Describe(ex);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    // ============================ 删除 / 清理 ============================

    /// <summary>删掉一份备份（连同它的元信息文件）。</summary>
    public static bool Delete(SaveBackupEntry entry)
    {
        try
        {
            if (File.Exists(entry.FilePath))
                File.Delete(entry.FilePath);

            var meta = MetaPath(entry.FilePath);
            if (File.Exists(meta))
                File.Delete(meta);

            Log.Information("已删除存档备份：{File}", entry.FilePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "删除存档备份失败：{File}", entry.FilePath);
            return false;
        }
    }

    /// <summary>
    /// 只保留最新的 <paramref name="keep"/> 份。
    ///
    /// <para>
    /// 返回删掉了几份。「恢复前留档」只在恢复时产生，跟着普通规则一起算份数 ——
    /// 否则玩家恢复几次就会攒下一堆谁也不敢删的保留档。
    /// </para>
    /// </summary>
    public static int Prune(string instanceId, int keep)
    {
        keep = Math.Max(1, keep);

        var all = List(instanceId);
        var stale = all.Skip(keep).ToList();

        var removed = 0;
        foreach (var entry in stale)
        {
            if (Delete(entry))
                removed++;
        }

        if (removed > 0)
            Log.Information("存档备份清理：{Instance} 保留 {Keep} 份，删掉 {Removed} 份", instanceId, keep, removed);

        return removed;
    }

    // ============================ 自动备份 ============================

    /// <summary>
    /// 启动游戏前调用：开关打开就备份一次并清理旧档。
    ///
    /// <para>
    /// 不抛异常 —— 它是启动路径上的一步，备份失败不该把游戏也拦住。
    /// </para>
    /// </summary>
    public static async Task<SaveBackupEntry?> TryAutoBackupAsync(string instanceId, IProgress<string>? status = null)
    {
        try
        {
            var cfg = SettingsService.Current;
            if (!cfg.AutoBackupSaves || string.IsNullOrWhiteSpace(instanceId))
                return null;

            var entry = await CreateAsync(instanceId, "auto", status).ConfigureAwait(false);
            if (entry is null)
                return null;

            Prune(instanceId, Math.Clamp(cfg.SaveBackupKeepCount, 1, 50));
            return entry;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "自动备份存档失败：{Instance}", instanceId);
            return null;
        }
    }

    /// <summary>打开某个实例的备份目录（没有就建出来）。</summary>
    public static void OpenFolder(string instanceId) => Open(FolderOf(instanceId));

    /// <summary>打开备份总目录（设置页用，不针对某个实例）。</summary>
    public static void OpenRootFolder() => Open(BackupRoot);

    private static void Open(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开备份目录失败：{Folder}", folder);
        }
    }

    // ============================ 元信息 ============================

    private sealed class BackupMeta
    {
        public string Reason { get; set; } = "manual";
        public List<string> Worlds { get; set; } = new();
    }

    private static string MetaPath(string zipPath) => zipPath + ".json";

    private static void WriteMeta(string zipPath, SaveBackupEntry entry)
    {
        try
        {
            var json = JsonSerializer.Serialize(new BackupMeta
            {
                Reason = entry.Reason,
                Worlds = entry.Worlds.ToList()
            }, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(MetaPath(zipPath), json);
        }
        catch (Exception ex)
        {
            // 元信息只是加速列表：丢了还能从文件名推回时间与原因
            Log.Debug(ex, "写入备份元信息失败：{File}", zipPath);
        }
    }

    private static BackupMeta? ReadMeta(string zipPath)
    {
        try
        {
            var path = MetaPath(zipPath);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<BackupMeta>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "清理恢复临时目录失败：{Dir}", dir);
        }
    }
}
