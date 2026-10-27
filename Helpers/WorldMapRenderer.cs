using System.IO.Compression;
using Serilog;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 一张渲染好的世界地图（BGRA 像素）。
///
/// <para>
/// 只包含「存档里已经生成过的区域」—— 没去过的区块是透明的，
/// 这让玩家能一眼看出自己探索到哪儿了。
/// </para>
/// </summary>
public sealed class WorldMap
{
    /// <summary>BGRA8888 像素，长度 = <see cref="PixelWidth"/> × <see cref="PixelHeight"/> × 4。</summary>
    public byte[] Pixels { get; init; } = [];

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    /// <summary>覆盖的区块宽度（<see cref="PixelWidth"/> = 它 × 16）。</summary>
    public int ChunkWidth { get; init; }

    /// <summary>覆盖的区块高度。</summary>
    public int ChunkHeight { get; init; }

    /// <summary>左上角对应的区块 X 坐标（可负）。</summary>
    public int MinChunkX { get; init; }

    /// <summary>左上角对应的区块 Z 坐标（可负）。</summary>
    public int MinChunkZ { get; init; }

    /// <summary>真正画出来的区块数（存档里已生成的）。</summary>
    public int FilledChunks { get; init; }

    /// <summary>需要额外告诉玩家一句话时才有值（例如「存档太大，只画了最近玩的那片」）。</summary>
    public string? Note { get; init; }

    /// <summary>覆盖范围的世界坐标（左上 / 右下），用于显示「这片区域在哪」。</summary>
    public (int X, int Z) WorldTopLeft => (MinChunkX * 16, MinChunkZ * 16);

    public (int X, int Z) WorldBottomRight => ((MinChunkX + ChunkWidth) * 16, (MinChunkZ + ChunkHeight) * 16);
}

/// <summary>
/// 把存档里<b>已经生成</b>的地形画成一张俯视图。
///
/// <para>
/// 这不是「按种子推算地图」：那需要整套生物群系生成算法（1.18 之后还是多噪声源叠加），
/// 既写不完也会随版本漂移。这里走的是另一条路 —— 直接读 <c>region/*.mca</c> 里每个区块的
/// 高度图（<c>Heightmaps.MOTION_BLOCKING</c>），按高度分层上色。数据是游戏自己算好写进去的，
/// 所以 1.12 到最新版都能画，永远和存档一致。
/// </para>
///
/// <para>
/// 代价是<b>只能看到去过的区域</b>。这对「边玩边看」其实正合适——没生成的区块本来也没有地形可看。
/// </para>
/// </summary>
public static class WorldMapRenderer
{
    /// <summary>一个区块占多少像素（16 = 一个方块一像素）。</summary>
    private const int PixelsPerChunk = 16;

    /// <summary>海平面。1.18 起地表整体抬高，但海平面仍是 63。</summary>
    private const int SeaLevel = 63;

    /// <summary>NBT 递归上限，防损坏区块把栈打穿。</summary>
    private const int MaxDepth = 32;

    /// <summary>一次渲染最多画多少区块 —— 超过就只画最近玩的那片（低端机上不能无限读）。</summary>
    private const int DefaultMaxChunks = 4096;

    /// <summary>地图最长边（像素）。再大就不是「浮窗里看一眼」了。</summary>
    private const int DefaultMaxPixels = 1024;

    /// <summary>在后台线程渲染（读盘 + 解压，别压在 UI 线程上）。</summary>
    public static Task<WorldMap?> RenderAsync(string worldFolder, CancellationToken ct = default)
        => Task.Run(() => Render(worldFolder, ct), ct);

    public static WorldMap? Render(string worldFolder, CancellationToken ct = default)
    {
        try
        {
            return RenderCore(worldFolder, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "渲染存档地图失败：{Folder}", worldFolder);
            return null;
        }
    }

    /// <summary>
    /// 找主世界的 <c>region</c> 目录。
    ///
    /// <para>
    /// 1.21.x 及以前区块平铺在 <c>&lt;world&gt;/region</c>；26.x 起按维度拆开，
    /// 主世界搬到 <c>&lt;world&gt;/dimensions/minecraft/overworld/region</c>。
    /// 两条路都得认，否则新存档会被画成一张空白。
    /// </para>
    ///
    /// <para>
    /// 模组可以改主世界的 id，所以最后再兜一遍「随便哪个维度有区块就用它」——
    /// 画出来的东西总比什么都不画有用。
    /// </para>
    /// </summary>
    private static string? ResolveRegionFolder(string worldFolder)
    {
        var legacy = Path.Combine(worldFolder, "region");
        if (HasRegionFiles(legacy))
            return legacy;

        var modern = Path.Combine(worldFolder, "dimensions", "minecraft", "overworld", "region");
        if (HasRegionFiles(modern))
            return modern;

        var dimensions = Path.Combine(worldFolder, "dimensions");
        try
        {
            if (Directory.Exists(dimensions))
            {
                foreach (var ns in Directory.GetDirectories(dimensions))
                {
                    foreach (var dim in Directory.GetDirectories(ns))
                    {
                        var candidate = Path.Combine(dim, "region");
                        if (HasRegionFiles(candidate))
                            return candidate;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "枚举 dimensions 目录失败：{Dir}", dimensions);
        }

        return null;
    }

    private static bool HasRegionFiles(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return false;

            foreach (var _ in Directory.EnumerateFiles(dir, "*.mca"))
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static WorldMap? RenderCore(string worldFolder, CancellationToken ct)
    {
        var regionDir = ResolveRegionFolder(worldFolder);
        if (regionDir is null)
            return null;

        var regions = CollectRegions(regionDir);
        if (regions.Count == 0)
            return null;

        // 从最近玩过的开始往里放，超界就不放 —— 玩家关心的永远是最近那片
        var kept = new List<RegionFile>();
        var bounds = (MinX: 0, MinZ: 0, MaxX: -1, MaxZ: -1);
        var trimmed = false;

        foreach (var region in regions)
        {
            // 用「真的有数据的区块」算包围盒，而不是整个 32×32 的 region
            var next = (MinX: bounds.MaxX < 0 ? region.MinChunkX : Math.Min(bounds.MinX, region.MinChunkX),
                        MinZ: bounds.MaxZ < 0 ? region.MinChunkZ : Math.Min(bounds.MinZ, region.MinChunkZ),
                        MaxX: Math.Max(bounds.MaxX, region.MaxChunkX),
                        MaxZ: Math.Max(bounds.MaxZ, region.MaxChunkZ));

            var width = (next.MaxX - next.MinX + 1) * PixelsPerChunk;
            var height = (next.MaxZ - next.MinZ + 1) * PixelsPerChunk;

            if (kept.Count > 0 && (width > DefaultMaxPixels || height > DefaultMaxPixels))
            {
                trimmed = true;
                continue;
            }

            kept.Add(region);
            bounds = next;
        }

        if (kept.Count == 0)
        {
            kept.Add(regions[0]);
            bounds = (regions[0].MinChunkX, regions[0].MinChunkZ, regions[0].MaxChunkX, regions[0].MaxChunkZ);
        }

        var chunkWidth = bounds.MaxX - bounds.MinX + 1;
        var chunkHeight = bounds.MaxZ - bounds.MinZ + 1;
        var pixelWidth = chunkWidth * PixelsPerChunk;
        var pixelHeight = chunkHeight * PixelsPerChunk;

        // 高度图：-1 = 该位置没有区块数据（没去过）
        var heights = new int[pixelWidth * pixelHeight];
        Array.Fill(heights, -1);

        var filled = 0;
        foreach (var region in kept)
        {
            ct.ThrowIfCancellationRequested();
            filled += ReadRegion(region, heights, pixelWidth, pixelHeight, bounds.MinX, bounds.MinZ, DefaultMaxChunks - filled, ct);

            if (filled >= DefaultMaxChunks)
            {
                trimmed = true;
                break;
            }
        }

        if (filled == 0)
            return null;

        var pixels = Paint(heights, pixelWidth, pixelHeight);

        return new WorldMap
        {
            Pixels = pixels,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            ChunkWidth = chunkWidth,
            ChunkHeight = chunkHeight,
            MinChunkX = bounds.MinX,
            MinChunkZ = bounds.MinZ,
            FilledChunks = filled,
            Note = trimmed ? "这个世界很大，只画了最近探索过的一片。" : null
        };
    }

    private readonly record struct RegionFile(
        string Path,
        int OriginChunkX,
        int OriginChunkZ,
        int MinChunkX,
        int MinChunkZ,
        int MaxChunkX,
        int MaxChunkZ,
        int ChunkCount,
        DateTime Modified);

    private static List<RegionFile> CollectRegions(string regionDir)
    {
        var list = new List<RegionFile>();

        foreach (var file in Directory.EnumerateFiles(regionDir, "r.*.mca"))
        {
            // 文件名形如 r.-1.2.mca
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split('.');
            if (parts.Length != 3)
                continue;

            if (!int.TryParse(parts[1], out var regionX) || !int.TryParse(parts[2], out var regionZ))
                continue;

            // 一个 region 覆盖 32×32 个区块；真正有数据的往往只有一小片，
            // 所以先只读 8 KB 头把范围扫出来 —— 否则 4 个区块的存档会被画成 512×512 的空图。
            var bounds = ScanBounds(file, regionX * 32, regionZ * 32, out var chunkCount);
            if (chunkCount == 0)
                continue;

            DateTime modified;
            try { modified = File.GetLastWriteTimeUtc(file); }
            catch { modified = DateTime.MinValue; }

            list.Add(new RegionFile(
                file,
                regionX * 32,
                regionZ * 32,
                bounds.MinX,
                bounds.MinZ,
                bounds.MaxX,
                bounds.MaxZ,
                chunkCount,
                modified));
        }

        list.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return list;
    }

    /// <summary>只读 region 的 8 KB 头，扫出「哪片区块真的有数据」。空 region 返回 count = 0。</summary>
    private static (int MinX, int MinZ, int MaxX, int MaxZ) ScanBounds(
        string path,
        int baseChunkX,
        int baseChunkZ,
        out int count)
    {
        count = 0;
        var minX = int.MaxValue;
        var minZ = int.MaxValue;
        var maxX = int.MinValue;
        var maxZ = int.MinValue;

        try
        {
            var header = new byte[4096];
            using var stream = File.OpenRead(path);

            var read = 0;
            while (read < header.Length)
            {
                var n = stream.Read(header, read, header.Length - read);
                if (n <= 0)
                    break;

                read += n;
            }

            if (read < header.Length)
                return (0, 0, 0, 0);

            for (var index = 0; index < 1024; index++)
            {
                var offset = (header[index * 4] << 16) | (header[index * 4 + 1] << 8) | header[index * 4 + 2];
                if (offset == 0)
                    continue;

                var x = baseChunkX + (index % 32);
                var z = baseChunkZ + (index / 32);

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
                count++;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "扫描区块文件范围失败：{Path}", path);
            count = 0;
        }

        return count == 0 ? (0, 0, 0, 0) : (minX, minZ, maxX, maxZ);
    }

    /// <summary>读一个 region 文件，把有数据的区块高度填进 <paramref name="heights"/>。返回画出的区块数。</summary>
    private static int ReadRegion(
        RegionFile region,
        int[] heights,
        int pixelWidth,
        int pixelHeight,
        int baseChunkX,
        int baseChunkZ,
        int budget,
        CancellationToken ct)
    {
        if (budget <= 0)
            return 0;

        var drawn = 0;

        try
        {
            using var stream = File.OpenRead(region.Path);

            // 8 KB 头：前 4 KB 是 1024 个「扇区偏移 + 扇区数」，后 4 KB 是时间戳（用不上）
            var header = new byte[4096];
            stream.ReadExactly(header);

            var lengthBuffer = new byte[5];

            for (var index = 0; index < 1024; index++)
            {
                if (drawn >= budget)
                    break;

                ct.ThrowIfCancellationRequested();

                var offset = (header[index * 4] << 16) | (header[index * 4 + 1] << 8) | header[index * 4 + 2];
                var sectors = header[index * 4 + 3];
                if (offset == 0 || sectors == 0)
                    continue;

                var localX = index % 32;
                var localZ = index / 32;
                var chunkX = region.OriginChunkX + localX - baseChunkX;
                var chunkZ = region.OriginChunkZ + localZ - baseChunkZ;

                // 被裁掉的 region 里的区块落在图外，直接跳过
                if (chunkX < 0 || chunkZ < 0)
                    continue;

                var pixelX = chunkX * PixelsPerChunk;
                var pixelZ = chunkZ * PixelsPerChunk;
                if (pixelX + PixelsPerChunk > pixelWidth || pixelZ + PixelsPerChunk > pixelHeight)
                    continue;

                int[]? chunkHeights;

                // 单个区块损坏（写一半 / 扇区被截断）不该让整个 region 一个都画不出来
                try
                {
                    stream.Seek(offset * 4096L, SeekOrigin.Begin);
                    stream.ReadExactly(lengthBuffer);

                    var length = (lengthBuffer[0] << 24) | (lengthBuffer[1] << 16) | (lengthBuffer[2] << 8) | lengthBuffer[3];
                    var compression = lengthBuffer[4];

                    if (length <= 1 || length > 64 * 1024 * 1024)
                        continue;

                    var payload = new byte[length - 1];
                    stream.ReadExactly(payload);

                    chunkHeights = ReadChunkHeights(payload, compression);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "跳过读不出来的区块：{Path} #{Index}", region.Path, index);
                    continue;
                }

                if (chunkHeights is null)
                    continue;

                for (var z = 0; z < 16; z++)
                {
                    var row = pixelZ + z;
                    if (row >= pixelHeight)
                        break;

                    var rowStart = row * pixelWidth + pixelX;
                    var sourceRow = z * 16;

                    for (var x = 0; x < 16; x++)
                    {
                        var h = chunkHeights[sourceRow + x];
                        if (h >= 0)
                            heights[rowStart + x] = h;
                    }
                }

                drawn++;
            }
        }
        catch (Exception ex)
        {
            // 单个 region 坏了不该让整张图失败
            Log.Debug(ex, "读取区块文件失败：{Path}", region.Path);
        }

        return drawn;
    }

    /// <summary>
    /// 从一个区块的 NBT 里取 16×16 的高度。取不到返回 null。
    ///
    /// <para>
    /// 1.14+ 在根或 <c>Level</c> 下的 <c>Heightmaps.MOTION_BLOCKING</c>（37 个 long，9 位打包）；
    /// 1.13 及更早在 <c>Level.HeightMap</c>（256 个 int）。两种都认，
    /// 所以老存档和新存档都能画。
    /// </para>
    /// </summary>
    private static int[]? ReadChunkHeights(byte[] payload, int compression)
    {
        try
        {
            using Stream raw = compression switch
            {
                1 => new GZipStream(new MemoryStream(payload), CompressionMode.Decompress),
                2 => new ZLibStream(new MemoryStream(payload), CompressionMode.Decompress),
                3 => new MemoryStream(payload),
                _ => throw new InvalidDataException($"不认识的区块压缩方式 {compression}")
            };

            using var reader = new BigEndianReader(raw);

            var rootType = reader.ReadByte();
            if (rootType != 10)
                return null;

            reader.ReadString();        // 根名字（通常是空串）

            var scan = new ChunkScan();
            ScanCompound(reader, scan, 0);

            // 方块数据优先，高度图只当兜底。
            //
            // 高度图是游戏为了少算几次而写的缓存，编码随版本变（26.3 就把 9 位打包换掉了，
            // 同一份 37 个 long 解出来全是 499/487 这种超出世界高度的值）。方块本身才是真相，
            // 而且 paletted container 的位打包从 1.13 到现在一直没动，只有调色板元素
            // 从 {Name:"..."} 简化成了字符串。
            if (scan.Sections.Count > 0)
                return HeightsFromSections(scan.Sections);

            return scan.HasHeightmap && scan.Heights.Any(h => h >= 0) ? scan.Heights : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "解析区块 NBT 失败");
            return null;
        }
    }

    /// <summary>
    /// 一趟扫描里要收集的东西：兜底用的高度图，和真正拿来算地表的方块数据。
    /// </summary>
    private sealed class ChunkScan
    {
        /// <summary>高度图解出来的 256 列（-1 = 没有数据）。只在没有方块数据时才用。</summary>
        public readonly int[] Heights = new int[256];

        /// <summary>是否真的解到过一份高度图。</summary>
        public bool HasHeightmap;

        /// <summary>各 section 的方块调色板与打包数据。</summary>
        public readonly List<SectionData> Sections = new();

        public ChunkScan() => Array.Fill(Heights, -1);
    }

    /// <summary>一个 16×16×16 区段：Y 索引、调色板、按位打包的方块索引。</summary>
    private sealed class SectionData
    {
        public int Y;

        /// <summary>方块 id 列表（<c>minecraft:stone</c> 这种全名）。</summary>
        public string[]? Palette;

        /// <summary>4096 个索引按位打包；为空表示整段只有 <see cref="Palette"/>[0] 一种方块。</summary>
        public long[]? Data;
    }

    /// <summary>遍历 compound，顺手捞走高度图与 sections —— 不按路径找，因为 1.18 前后根结构不一样。</summary>
    private static void ScanCompound(BigEndianReader reader, ChunkScan scan, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException("区块 NBT 嵌套过深");

        while (true)
        {
            var type = reader.ReadByte();
            if (type == 0)
                return;

            var name = reader.ReadString();
            ReadPayload(reader, type, name, scan, depth);
        }
    }

    private static void ReadPayload(BigEndianReader reader, int type, string name, ChunkScan scan, int depth)
    {
        switch (type)
        {
            case 1:     // Byte
                reader.ReadByte();
                break;
            case 2:     // Short
                reader.ReadInt16();
                break;
            case 3:     // Int
                reader.ReadInt32();
                break;
            case 4:     // Long
                reader.ReadInt64();
                break;
            case 5:     // Float
                reader.ReadSingle();
                break;
            case 6:     // Double
                reader.ReadDouble();
                break;
            case 7:     // ByteArray
                reader.Skip(reader.ReadInt32());
                break;
            case 8:     // String
                reader.ReadString();
                break;
            case 9:     // List
            {
                // sections 要按区段结构单独读，不能像别处那样一视同仁地递归跳过
                if (name.Equals("sections", StringComparison.OrdinalIgnoreCase))
                {
                    ReadSections(reader, scan);
                    break;
                }

                var itemType = reader.ReadByte();
                var count = reader.ReadInt32();

                // 防止损坏文件声称有十亿个元素
                if (count < 0 || count > 4_000_000)
                    throw new InvalidDataException("区块 NBT 列表长度异常");

                for (var i = 0; i < count; i++)
                    ReadPayload(reader, itemType, string.Empty, scan, depth + 1);

                break;
            }
            case 10:    // Compound
                ScanCompound(reader, scan, depth + 1);
                break;
            case 11:    // IntArray
            {
                var count = reader.ReadInt32();
                if (count < 0 || count > 4_000_000)
                    throw new InvalidDataException("区块 NBT 数组长度异常");

                // 1.13 ~ 1.15 的字段名是 Heightmap（不是 HeightMap），大小写不能写死
                if (count == 256 && name.Equals("Heightmap", StringComparison.OrdinalIgnoreCase))
                {
                    for (var i = 0; i < 256; i++)
                        scan.Heights[i] = reader.ReadInt32();

                    scan.HasHeightmap = true;
                }
                else
                {
                    reader.Skip(count * 4);
                }

                break;
            }
            case 12:    // LongArray
            {
                var count = reader.ReadInt32();
                if (count < 0 || count > 4_000_000)
                    throw new InvalidDataException("区块 NBT 数组长度异常");

                if ((name == "MOTION_BLOCKING" || name == "WORLD_SURFACE") && count >= 37 && count <= 128)
                {
                    var words = new long[count];
                    for (var i = 0; i < count; i++)
                        words[i] = reader.ReadInt64();

                    if (Unpack9Bit(words, scan.Heights))
                        scan.HasHeightmap = true;
                }
                else
                {
                    reader.Skip(count * 8);
                }

                break;
            }
            default:
                throw new InvalidDataException($"未知的 NBT 标签类型 {type}");
        }
    }

    // ============================ 方块数据 → 地表高度 ============================

    /// <summary>读 <c>sections</c> 列表，把每个区段的调色板和打包数据收下来。</summary>
    private static void ReadSections(BigEndianReader reader, ChunkScan scan)
    {
        var itemType = reader.ReadByte();
        var count = reader.ReadInt32();

        if (count < 0 || count > 4096)
            throw new InvalidDataException("区块 sections 数量异常");

        for (var i = 0; i < count; i++)
        {
            if (itemType != 10)
            {
                SkipPayload(reader, itemType);
                continue;
            }

            var section = ReadSection(reader);
            if (section is not null)
                scan.Sections.Add(section);
        }
    }

    private static SectionData? ReadSection(BigEndianReader reader)
    {
        var section = new SectionData();

        while (true)
        {
            var type = reader.ReadByte();
            if (type == 0)
                break;

            var name = reader.ReadString();

            if (type == 1 && name == "Y")
            {
                // MC 把 Y 存成 TAG_Byte，是有符号的（-4 = 世界最底下的区段）
                section.Y = unchecked((sbyte)reader.ReadByte());
            }
            else if (type == 10 && name == "block_states")
            {
                ReadBlockStates(reader, section);
            }
            else
            {
                SkipPayload(reader, type);
            }
        }

        return section.Palette is { Length: > 0 } ? section : null;
    }

    private static void ReadBlockStates(BigEndianReader reader, SectionData section)
    {
        while (true)
        {
            var type = reader.ReadByte();
            if (type == 0)
                return;

            var name = reader.ReadString();

            if (type == 9 && name == "palette")
            {
                section.Palette = ReadPalette(reader);
            }
            else if (type == 12 && name == "data")
            {
                var count = reader.ReadInt32();
                if (count <= 0 || count > 8192)
                {
                    if (count > 0)
                        reader.Skip(count * 8);

                    continue;
                }

                var words = new long[count];
                for (var i = 0; i < count; i++)
                    words[i] = reader.ReadInt64();

                section.Data = words;
            }
            else
            {
                SkipPayload(reader, type);
            }
        }
    }

    /// <summary>
    /// 读区段的方块调色板。两种编码都认：
    /// 1.13 ~ 1.21.x 是 <c>List&lt;Compound{Name}&gt;</c>，
    /// 26.x 起简化成了 <c>List&lt;String&gt;</c>（<c>minecraft:bedrock</c> 这样）。
    /// </summary>
    private static string[]? ReadPalette(BigEndianReader reader)
    {
        var itemType = reader.ReadByte();
        var count = reader.ReadInt32();

        if (count <= 0 || count > 65_536)
            return null;

        var palette = new string[count];

        if (itemType == 8)
        {
            for (var i = 0; i < count; i++)
                palette[i] = reader.ReadString();
        }
        else if (itemType == 10)
        {
            for (var i = 0; i < count; i++)
                palette[i] = ReadBlockName(reader) ?? "minecraft:air";
        }
        else
        {
            throw new InvalidDataException("区块调色板元素类型不认识");
        }

        return palette;
    }

    private static string? ReadBlockName(BigEndianReader reader)
    {
        string? name = null;

        while (true)
        {
            var type = reader.ReadByte();
            if (type == 0)
                return name;

            var key = reader.ReadString();

            // 两代编码都认：旧版是 {"Name": "minecraft:stone"}，
            // 26.x 把键名省成了空串（{"": "minecraft:stone"}），每项省 4 字节。
            if (type == 8 && name is null && (key == "Name" || key.Length == 0))
            {
                name = reader.ReadString();
            }
            else
            {
                SkipPayload(reader, type);
            }
        }
    }

    /// <summary>
    /// 从各区段的方块数据算出每列最高的非空气方块 —— 也就是地表。
    ///
    /// <para>
    /// 从最高的区段往下扫，某列一旦有答案就锁定，不再被更低的方块覆盖。
    /// 正常地形上上面两三个区段就扫完了，所以实际开销比解一遍高度图还小。
    /// </para>
    /// </summary>
    private static int[]? HeightsFromSections(List<SectionData> sections)
    {
        var heights = new int[256];
        Array.Fill(heights, -1);

        var pending = 256;

        foreach (var section in sections.OrderByDescending(s => s.Y))
        {
            if (pending == 0)
                break;

            if (section.Palette is not { Length: > 0 } palette)
                continue;

            var baseY = section.Y * 16;

            // 没有 data 说明整段只有调色板里那一种方块
            if (section.Data is null)
            {
                if (IsAir(palette[0]))
                    continue;

                for (var column = 0; column < 256; column++)
                {
                    if (heights[column] >= 0)
                        continue;

                    heights[column] = baseY + 15;
                    pending--;
                }

                continue;
            }

            var bits = Math.Max(4, BitsFor(palette.Length));
            var perLong = 64 / bits;
            var mask = (1UL << bits) - 1;

            // 索引顺序是 y*256 + z*16 + x：x 变得最快，所以低 8 位就是列号
            for (var index = 0; index < 4096 && pending > 0; index++)
            {
                var column = index & 255;
                if (heights[column] >= 0)
                    continue;

                var wordIndex = index / perLong;
                if (wordIndex >= section.Data.Length)
                    break;

                var word = (ulong)section.Data[wordIndex];
                var value = (int)((word >> ((index % perLong) * bits)) & mask);

                // 越界说明这份 data 和调色板对不上（区块改过之后残留的旧数组）。
                // 当成空气跳过 —— 当成方块会画出一堆凭空冒出来的高地。
                if (value >= palette.Length || IsAir(palette[value]))
                    continue;

                heights[column] = baseY + (index >> 8);
                pending--;
            }
        }

        return heights.Any(h => h >= 0) ? heights : null;
    }

    private static int BitsFor(int count)
    {
        var bits = 0;
        while ((1L << bits) < count)
            bits++;

        return bits;
    }

    private static bool IsAir(string block)
    {
        var colon = block.IndexOf(':');
        var id = colon >= 0 ? block[(colon + 1)..] : block;

        return id switch
        {
            "air" or "cave_air" or "void_air" => true,
            _ => false
        };
    }

    /// <summary>只看长度、不关心内容的标签，用来跳过不认识的字段。</summary>
    private static void SkipPayload(BigEndianReader reader, int type)
    {
        switch (type)
        {
            case 1: reader.ReadByte(); break;
            case 2: reader.ReadInt16(); break;
            case 3: reader.ReadInt32(); break;
            case 4: reader.ReadInt64(); break;
            case 5: reader.ReadSingle(); break;
            case 6: reader.ReadDouble(); break;

            case 7:
            {
                var count = reader.ReadInt32();
                if (count > 0)
                    reader.Skip(count);

                break;
            }

            case 8: reader.ReadString(); break;

            case 9:
            {
                var itemType = reader.ReadByte();
                var count = reader.ReadInt32();

                if (count < 0 || count > 4_000_000)
                    throw new InvalidDataException("区块 NBT 列表长度异常");

                for (var i = 0; i < count; i++)
                    SkipPayload(reader, itemType);

                break;
            }

            case 10:
                while (true)
                {
                    var inner = reader.ReadByte();
                    if (inner == 0)
                        break;

                    reader.ReadString();
                    SkipPayload(reader, inner);
                }

                break;

            case 11:
            {
                var count = reader.ReadInt32();
                if (count > 0)
                    reader.Skip(count * 4);

                break;
            }

            case 12:
            {
                var count = reader.ReadInt32();
                if (count > 0)
                    reader.Skip(count * 8);

                break;
            }

            default:
                throw new InvalidDataException($"未知的 NBT 标签类型 {type}");
        }
    }

    /// <summary>
    /// 把 9 位打包的 256 个高度值拆出来。成功拆出一份就返回 true。
    ///
    /// <para>
    /// MC 的位打包<b>不是</b>连续位流：每个 long 里独立塞满 <c>64 / 9 = 7</c> 个值，
    /// 剩下的 1 位直接浪费掉，所以 256 个值固定占 37 个 long —— 这不是 padding，是算法本身。
    /// 按「跨 long 连续位流」去解会得到一片乱码。
    /// </para>
    ///
    /// <para>
    /// ⚠ 这只是<b>兜底</b>路径。26.3 起这份缓存的编码又变了（同样的 37 个 long 解出来会是
    /// 499/487 这类超出世界高度的值），所以正常情况下走 <see cref="HeightsFromSections"/>。
    /// </para>
    /// </summary>
    private static bool Unpack9Bit(long[] words, int[] heights)
    {
        const int bitsPerValue = 9;
        const int mask = (1 << bitsPerValue) - 1;
        const int valuesPerLong = 64 / bitsPerValue;                 // 7
        var required = (256 + valuesPerLong - 1) / valuesPerLong;    // 37

        if (words.Length < required)
            return false;

        for (var i = 0; i < 256; i++)
        {
            var word = (ulong)words[i / valuesPerLong];
            var shift = (i % valuesPerLong) * bitsPerValue;
            heights[i] = (int)((word >> shift) & mask);
        }

        return true;
    }

    // ============================ 上色 ============================

    /// <summary>把高度表画成 BGRA 像素。没数据的格子保持全透明（alpha = 0）。</summary>
    private static byte[] Paint(int[] heights, int width, int height)
    {
        var pixels = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var h = heights[index];
                if (h < 0)
                    continue;

                var (r, g, b) = ColorFor(h);

                // 西北打光的简易浮雕：和左边、上边的落差决定明暗
                var left = x > 0 && heights[index - 1] >= 0 ? heights[index - 1] : h;
                var top = y > 0 && heights[index - width] >= 0 ? heights[index - width] : h;
                var slope = (h - left) * 0.06f + (h - top) * 0.09f;
                var shade = Math.Clamp(1f + slope, 0.68f, 1.32f);

                var offset = index * 4;
                pixels[offset] = (byte)Math.Clamp(b * shade, 0, 255);       // B
                pixels[offset + 1] = (byte)Math.Clamp(g * shade, 0, 255);   // G
                pixels[offset + 2] = (byte)Math.Clamp(r * shade, 0, 255);   // R
                pixels[offset + 3] = 255;                                   // A
            }
        }

        return pixels;
    }

    private static (float R, float G, float B) ColorFor(int height)
    {
        // 水下：越深越沉
        if (height <= SeaLevel)
        {
            var depth = Math.Clamp(SeaLevel - height, 0, 40);
            var t = depth / 40f;
            return (Lerp(90, 16, t), Lerp(150, 40, t), Lerp(205, 88, t));
        }

        // 沙滩
        if (height <= SeaLevel + 2)
            return (226, 214, 168);

        // 草原 / 丘陵
        if (height <= 88)
        {
            var t = (height - SeaLevel) / 25f;
            return (Lerp(96, 118, t), Lerp(150, 168, t), Lerp(78, 100, t));
        }

        // 高原草甸
        if (height <= 100)
            return (122, 136, 96);

        // 裸岩
        if (height <= 116)
            return (138, 131, 120);

        // 雪线以上
        return (232, 237, 242);
    }

    private static float Lerp(float from, float to, float t) => from + ((to - from) * Math.Clamp(t, 0, 1));
}
