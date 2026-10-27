using System.Text;
using Serilog;

namespace VibrantbitLauncher.Helpers;

/// <summary>本地存档（<c>saves/&lt;目录&gt;</c>）。</summary>
public sealed class WorldSave
{
    /// <summary>存档目录的完整路径。</summary>
    public string FolderPath { get; init; } = string.Empty;

    /// <summary>存档目录名（<c>saves</c> 下那一层）。</summary>
    public string FolderName { get; init; } = string.Empty;

    /// <summary>游戏里的存档名（<c>level.dat</c> 里的 <c>LevelName</c>）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 这个存档来自哪个 saves 目录（给人看的标签）。
    /// 版本隔离下会有多份，靠它在下拉框里区分同名存档。
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>世界种子；读不出来时为 null（例如加密 / 新版改结构 / 文件损坏）。</summary>
    public long? Seed { get; init; }

    /// <summary>下拉框里显示的文字。</summary>
    public string DisplayName => Seed.HasValue
        ? $"{Name}（{Seed.Value}）"
        : $"{Name}（种子未知）";
}

/// <summary>
/// 从存档里读世界种子。
///
/// <para>
/// <c>level.dat</c> 是 <b>gzip 压缩的 NBT</b>（二进制标签树，大端序）。这里只做一件很窄的事：
/// 解出根 compound，按 <c>Data.WorldGenSettings.seed</c>（1.16+）或 <c>Data.RandomSeed</c>（更早）
/// 取那个长整数，顺带取 <c>Data.LevelName</c> 当存档名。
/// </para>
///
/// <para>
/// 不引第三方 NBT 库是有意的：为了两个字段多背一个依赖不划算，而 NBT 的标签种类是封闭集合，
/// 一个两百行的读取器就能覆盖完整格式。读失败一律返回 null（种子读不出来不该让界面报错）。
/// </para>
///
/// <para>
/// 地形由 <see cref="WorldMapRenderer"/> 从 <c>region/*.mca</c> 里读 —— 那是游戏自己写进存档的
/// 高度图，不按种子推算，所以离线、跨版本、即时。
/// </para>
/// </summary>
public static class WorldSeedReader
{
    /// <summary>NBT 嵌套最大深度，防损坏文件把递归撑爆。</summary>
    private const int MaxDepth = 16;

    /// <summary>
    /// 枚举一个 <c>saves</c> 目录下的存档。目录不存在时返回空表。
    /// </summary>
    /// <param name="savesFolder">存档目录。</param>
    /// <param name="sourceLabel">来源标签（写进 <see cref="WorldSave.Source"/>，下拉框里用）。</param>
    public static List<WorldSave> ReadWorlds(string? savesFolder, string? sourceLabel = null)
    {
        var result = new List<WorldSave>();

        if (string.IsNullOrWhiteSpace(savesFolder) || !Directory.Exists(savesFolder))
            return result;

        try
        {
            foreach (var dir in Directory.GetDirectories(savesFolder))
            {
                var levelDat = Path.Combine(dir, "level.dat");
                if (!File.Exists(levelDat))
                    continue;

                var folderName = Path.GetFileName(dir);
                var (name, seed) = ReadLevelDat(levelDat);

                result.Add(new WorldSave
                {
                    FolderPath = dir,
                    FolderName = folderName,
                    Name = string.IsNullOrWhiteSpace(name) ? folderName : name!,
                    Source = sourceLabel ?? string.Empty,
                    Seed = seed
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "枚举存档目录失败：{Dir}", savesFolder);
        }

        // 最近玩过的排前面：level.dat 的写入时间就是「上次进入这个存档」的粗粒度代理
        result.Sort((a, b) => GetWriteTime(b).CompareTo(GetWriteTime(a)));
        return result;
    }

    /// <summary>
    /// 把多个 <c>saves</c> 目录的存档汇总成一张表。
    ///
    /// <para>
    /// 为什么不是「一个目录」：开了版本隔离时存档在 <c>versions/&lt;id&gt;/saves</c>，
    /// 没开就在根 <c>saves</c>，而用户还可能手动把存档目录挪到别处。
    /// 只知道「大概在这几处」是常态，所以全扫一遍再按真实路径去重。
    /// </para>
    /// </summary>
    public static List<WorldSave> ReadAllWorlds(IEnumerable<(string Path, string Label)> folders)
    {
        var result = new List<WorldSave>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, label) in folders)
        {
            foreach (var world in ReadWorlds(path, label))
            {
                // 同目录被重复列出（例如根 saves 被链到版本目录）时只留一份
                string key;
                try { key = Path.GetFullPath(world.FolderPath); }
                catch { key = world.FolderPath; }

                if (seen.Add(key))
                    result.Add(world);
            }
        }

        result.Sort((a, b) => GetWriteTime(b).CompareTo(GetWriteTime(a)));
        return result;
    }

    private static DateTime GetWriteTime(WorldSave save)
    {
        try { return File.GetLastWriteTimeUtc(Path.Combine(save.FolderPath, "level.dat")); }
        catch { return DateTime.MinValue; }
    }
    /// <summary>读某个 <c>level.dat</c> 的存档名与种子。</summary>
    public static (string? Name, long? Seed) ReadLevelDat(string levelDatPath)
    {
        try
        {
            string? name = null;
            long? seed = null;

            using (var file = File.OpenRead(levelDatPath))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
            using (var reader = new BigEndianReader(gzip))
            {
                // 根标签：类型必须是一个具名 compound
                var type = reader.ReadByte();
                if (type == NbtTag.Compound)
                {
                    reader.ReadString();                       // 根名字（通常是空串）
                    var root = ReadCompound(reader, 0);        // 根 compound 的载荷

                    if (root is not null
                        && root.TryGetValue("Data", out var dataObj)
                        && dataObj is Dictionary<string, object?> data)
                    {
                        name = data.TryGetValue("LevelName", out var nameObj) ? nameObj as string : null;
                        seed = FindSeed(data);
                    }
                }
            }

            // 26.x 起世界生成设置被搬出了 level.dat，单独放在存档的 data 目录下
            seed ??= ReadSplitWorldGenSeed(Path.GetDirectoryName(levelDatPath));

            return (name, seed);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取 level.dat 失败：{Path}", levelDatPath);
            return (null, null);
        }
    }

    /// <summary>
    /// 读 26.x 新布局的种子：<c>&lt;world&gt;/data/minecraft/world_gen_settings.dat</c>。
    ///
    /// <para>
    /// 新版把世界生成设置整块从 <c>level.dat</c> 里搬了出来（同一批改动还把每个维度的区块
    /// 拆进了 <c>dimensions/</c>）。文件是 gzip NBT，根下是小写的 <c>data</c>，
    /// 种子就是里面的 <c>seed</c> —— 别按旧版的 <c>Data.WorldGenSettings.seed</c> 去找。
    /// </para>
    /// </summary>
    private static long? ReadSplitWorldGenSeed(string? worldFolder)
    {
        if (string.IsNullOrWhiteSpace(worldFolder))
            return null;

        var path = Path.Combine(worldFolder, "data", "minecraft", "world_gen_settings.dat");
        if (!File.Exists(path))
            return null;

        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new BigEndianReader(gzip);

            if (reader.ReadByte() != NbtTag.Compound)
                return null;

            reader.ReadString();
            var root = ReadCompound(reader, 0);
            if (root is null)
                return null;

            foreach (var key in SplitSeedContainers)
            {
                if (root.TryGetValue(key, out var node)
                    && node is Dictionary<string, object?> dict
                    && dict.TryGetValue("seed", out var seedObj)
                    && TryToLong(seedObj, out var seed))
                {
                    return seed;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取 world_gen_settings.dat 失败：{Path}", path);
            return null;
        }
    }

    /// <summary><c>world_gen_settings.dat</c> 里可能包着种子的那一层。</summary>
    private static readonly string[] SplitSeedContainers =
        { "data", "Data", "WorldGenSettings", "world_gen_settings" };

    /// <summary>
    /// 在 <c>Data</c> 里找种子：1.16 起在 <c>WorldGenSettings.seed</c>，
    /// 更早的版本在顶层的 <c>RandomSeed</c>。两个都试，先新后旧。
    /// </summary>
    private static long? FindSeed(Dictionary<string, object?> data)
    {
        if (data.TryGetValue("WorldGenSettings", out var wgsObj) && wgsObj is Dictionary<string, object?> wgs
            && wgs.TryGetValue("seed", out var modernSeed)
            && TryToLong(modernSeed, out var modern))
        {
            return modern;
        }

        if (data.TryGetValue("RandomSeed", out var legacySeed) && TryToLong(legacySeed, out var legacy))
            return legacy;

        return null;
    }

    private static bool TryToLong(object? value, out long result)
    {
        switch (value)
        {
            case long l: result = l; return true;
            case int i: result = i; return true;
            case short s: result = s; return true;
            case sbyte b: result = b; return true;
            default: result = 0; return false;
        }
    }

    /// <summary>在线种子地图的地址（Chunkbase，国内可访问，按种子直接落位）。</summary>
    public static string BuildSeedMapUrl(long seed)
        => $"https://www.chunkbase.com/apps/seed-map#seed={seed}";

    // ============================ NBT ============================

    private static class NbtTag
    {
        public const byte End = 0;
        public const byte Byte = 1;
        public const byte Short = 2;
        public const byte Int = 3;
        public const byte Long = 4;
        public const byte Float = 5;
        public const byte Double = 6;
        public const byte ByteArray = 7;
        public const byte String = 8;
        public const byte List = 9;
        public const byte Compound = 10;
        public const byte IntArray = 11;
        public const byte LongArray = 12;
    }

    /// <summary>读一个 compound 的载荷（不含自己的类型字节与名字）。遇到 <c>TAG_End</c> 结束。</summary>
    private static Dictionary<string, object?>? ReadCompound(BigEndianReader reader, int depth)
    {
        if (depth > MaxDepth)
            return null;

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        while (true)
        {
            var type = reader.ReadByte();
            if (type == NbtTag.End)
                break;

            var name = reader.ReadString();
            result[name] = ReadPayload(reader, type, depth + 1);
        }

        return result;
    }

    private static object? ReadPayload(BigEndianReader reader, byte type, int depth)
    {
        if (depth > MaxDepth)
            return null;

        switch (type)
        {
            case NbtTag.Byte:
                return (sbyte)reader.ReadByte();

            case NbtTag.Short:
                return reader.ReadInt16();

            case NbtTag.Int:
                return reader.ReadInt32();

            case NbtTag.Long:
                return reader.ReadInt64();

            case NbtTag.Float:
                return reader.ReadSingle();

            case NbtTag.Double:
                return reader.ReadDouble();

            case NbtTag.ByteArray:
            {
                var length = reader.ReadInt32();
                if (length < 0)
                    return null;

                reader.Skip(length);
                return null;      // 内容用不上，跳过即可
            }

            case NbtTag.String:
                return reader.ReadString();

            case NbtTag.List:
            {
                var elementType = reader.ReadByte();
                var length = reader.ReadInt32();
                if (length < 0)
                    return null;

                for (var i = 0; i < length; i++)
                {
                    var item = ReadPayload(reader, elementType, depth + 1);

                    // 列表里只关心 compound（种子在 WorldGenSettings 里，而它是 compound 不是列表元素），
                    // 其它一律跳过 —— 但仍要读完，否则后面的字段会错位。
                    _ = item;
                }

                return null;
            }

            case NbtTag.Compound:
                return ReadCompound(reader, depth + 1);

            case NbtTag.IntArray:
            {
                var length = reader.ReadInt32();
                if (length < 0)
                    return null;

                reader.Skip(length * 4);
                return null;
            }

            case NbtTag.LongArray:
            {
                var length = reader.ReadInt32();
                if (length < 0)
                    return null;

                reader.Skip(length * 8);
                return null;
            }

            default:
                // 不认识的标签类型：无法知道它有多长，只能中止（返回 null 让上层放弃）
                throw new InvalidDataException($"未知的 NBT 标签类型 {type}");
        }
    }
}
