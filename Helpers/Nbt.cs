using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>NBT 的标签类型（数值即磁盘上的类型字节，不可改）。</summary>
    public enum NbtType : byte
    {
        End = 0,
        Byte = 1,
        Short = 2,
        Int = 3,
        Long = 4,
        Float = 5,
        Double = 6,
        ByteArray = 7,
        String = 8,
        List = 9,
        Compound = 10,
        IntArray = 11,
        LongArray = 12,
    }

    /// <summary>
    /// 一棵 NBT 树。
    ///
    /// <para>
    /// 这里只实现「读进来 → 改一个字段 → 写回去」，所以刻意做成**通用树**而不是强类型模型：
    /// <c>servers.dat</c> 里除了 name / ip 还有 icon（几十 KB 的 base64）、acceptTextures、
    /// 以及各版本陆续加的 hidden 之类字段。做成强类型就会有「不认识的标签被丢掉」的风险，
    /// 而丢掉一次就等于把玩家攒了很久的服务器列表洗掉了。
    /// </para>
    /// </summary>
    public sealed class NbtTag
    {
        public NbtType Type { get; init; }

        public string Name { get; set; } = string.Empty;

        /// <summary>标量值：byte / short / int / long / float / double / string / byte[] / int[] / long[]。</summary>
        public object? Value { get; set; }

        /// <summary>List 的元素类型。空列表也必须写对，否则游戏读的时候会当成异类列表而报错。</summary>
        public NbtType ElementType { get; set; } = NbtType.End;

        /// <summary>Compound 的子标签 / List 的元素（元素自身的 Name 无意义）。</summary>
        public List<NbtTag> Items { get; } = new();

        public static NbtTag Compound(string name = "") => new() { Type = NbtType.Compound, Name = name };

        public static NbtTag List(string name, NbtType elementType)
            => new() { Type = NbtType.List, Name = name, ElementType = elementType };

        public static NbtTag String(string name, string value)
            => new() { Type = NbtType.String, Name = name, Value = value };

        public static NbtTag Byte(string name, byte value)
            => new() { Type = NbtType.Byte, Name = name, Value = value };

        /// <summary>找直接子标签（Compound 用）。</summary>
        public NbtTag? Get(string name)
        {
            foreach (var item in Items)
            {
                if (string.Equals(item.Name, name, StringComparison.Ordinal))
                    return item;
            }

            return null;
        }

        /// <summary>取字符串子标签的值；不存在或类型不对时返回 null。</summary>
        public string? GetString(string name)
            => Get(name) is { Type: NbtType.String } tag ? tag.Value as string : null;

        public void Remove(string name)
            => Items.RemoveAll(i => string.Equals(i.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// NBT 读取（大端序，Java 的 <c>NbtIo.read</c> 同款）。
    /// </summary>
    public static class NbtReader
    {
        /// <summary>防御性上限：正常 servers.dat 嵌套不到这个深度，超了说明文件已经坏了。</summary>
        private const int MaxDepth = 64;

        /// <summary>字符串长度上限，防止坏文件申请出几个 G 的内存。</summary>
        private const int MaxStringLength = 4 * 1024 * 1024;

        /// <summary>
        /// 读一个命名标签作为根。文件为空 / 立刻遇到 End 时返回 null。
        /// </summary>
        public static NbtTag? Read(Stream stream)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            var type = (NbtType)reader.ReadByte();
            if (type == NbtType.End)
                return null;

            var name = ReadString(reader);
            return ReadPayload(reader, type, name, 0);
        }

        private static NbtTag ReadPayload(BinaryReader reader, NbtType type, string name, int depth)
        {
            if (depth > MaxDepth)
                throw new InvalidDataException("NBT 嵌套过深");

            switch (type)
            {
                case NbtType.Byte:
                    return new NbtTag { Type = type, Name = name, Value = reader.ReadByte() };

                case NbtType.Short:
                    return new NbtTag { Type = type, Name = name, Value = ReadInt16BE(reader) };

                case NbtType.Int:
                    return new NbtTag { Type = type, Name = name, Value = ReadInt32BE(reader) };

                case NbtType.Long:
                    return new NbtTag { Type = type, Name = name, Value = ReadInt64BE(reader) };

                case NbtType.Float:
                    return new NbtTag { Type = type, Name = name, Value = ReadSingleBE(reader) };

                case NbtType.Double:
                    return new NbtTag { Type = type, Name = name, Value = ReadDoubleBE(reader) };

                case NbtType.String:
                    return new NbtTag { Type = type, Name = name, Value = ReadString(reader) };

                case NbtType.ByteArray:
                {
                    var length = ReadLength(reader);
                    var bytes = reader.ReadBytes(length);
                    if (bytes.Length != length)
                        throw new EndOfStreamException("ByteArray 被截断");
                    return new NbtTag { Type = type, Name = name, Value = bytes };
                }

                case NbtType.IntArray:
                {
                    var length = ReadLength(reader);
                    var values = new int[length];
                    for (var i = 0; i < length; i++)
                        values[i] = ReadInt32BE(reader);
                    return new NbtTag { Type = type, Name = name, Value = values };
                }

                case NbtType.LongArray:
                {
                    var length = ReadLength(reader);
                    var values = new long[length];
                    for (var i = 0; i < length; i++)
                        values[i] = ReadInt64BE(reader);
                    return new NbtTag { Type = type, Name = name, Value = values };
                }

                case NbtType.List:
                {
                    var elementType = (NbtType)reader.ReadByte();
                    var length = ReadLength(reader);

                    var list = NbtTag.List(name, elementType);

                    // 元素为 End 时长度必然是 0；不认识的组合按空列表处理，不要抛。
                    if (elementType == NbtType.End || length == 0)
                        return list;

                    for (var i = 0; i < length; i++)
                        list.Items.Add(ReadPayload(reader, elementType, string.Empty, depth + 1));

                    return list;
                }

                case NbtType.Compound:
                {
                    var compound = NbtTag.Compound(name);

                    while (true)
                    {
                        var childType = (NbtType)reader.ReadByte();
                        if (childType == NbtType.End)
                            break;

                        var childName = ReadString(reader);
                        compound.Items.Add(ReadPayload(reader, childType, childName, depth + 1));
                    }

                    return compound;
                }

                default:
                    throw new InvalidDataException($"未知的 NBT 类型 0x{(byte)type:X2}");
            }
        }

        private static int ReadLength(BinaryReader reader)
        {
            var length = ReadInt32BE(reader);
            if (length < 0 || length > MaxStringLength * 16)
                throw new InvalidDataException($"NBT 长度异常：{length}");
            return length;
        }

        private static string ReadString(BinaryReader reader)
        {
            // ⚠️ 必须自己按大端读：BinaryReader.ReadUInt16 是**小端**的，
            // 用了它长度就会整体偏移，读出来的字符串全乱（1.0.31.0 的初版就是这样）。
            var length = (int)((reader.ReadByte() << 8) | reader.ReadByte());

            if (length == 0)
                return string.Empty;

            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException("字符串被截断");

            return JavaUtf8.Decode(bytes);
        }

        private static short ReadInt16BE(BinaryReader r)
        {
            var b = r.ReadBytes(2);
            return (short)((b[0] << 8) | b[1]);
        }

        private static int ReadInt32BE(BinaryReader r)
        {
            var b = r.ReadBytes(4);
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        private static long ReadInt64BE(BinaryReader r)
        {
            var b = r.ReadBytes(8);
            long value = 0;
            for (var i = 0; i < 8; i++)
                value = (value << 8) | b[i];
            return value;
        }

        private static float ReadSingleBE(BinaryReader r)
        {
            var b = r.ReadBytes(4);
            Array.Reverse(b);
            return BitConverter.ToSingle(b, 0);
        }

        private static double ReadDoubleBE(BinaryReader r)
        {
            var b = r.ReadBytes(8);
            Array.Reverse(b);
            return BitConverter.ToDouble(b, 0);
        }
    }

    /// <summary>
    /// NBT 写出（大端序）。读进来的树原样写回去，只改我们动过的那几个字段。
    /// </summary>
    public static class NbtWriter
    {
        /// <summary>把树写成「带根名字的整份文件」。空树会被拒绝 —— 半个文件比没有文件更糟。</summary>
        public static void Write(Stream stream, NbtTag root)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            writer.Write((byte)root.Type);
            WriteString(writer, root.Name);
            WritePayload(writer, root);
            writer.Flush();
        }

        private static void WritePayload(BinaryWriter writer, NbtTag tag)
        {
            switch (tag.Type)
            {
                case NbtType.Byte:
                    writer.Write((byte)(tag.Value ?? (byte)0));
                    break;

                case NbtType.Short:
                    WriteInt16BE(writer, (short)(tag.Value ?? (short)0));
                    break;

                case NbtType.Int:
                    WriteInt32BE(writer, (int)(tag.Value ?? 0));
                    break;

                case NbtType.Long:
                    WriteInt64BE(writer, (long)(tag.Value ?? 0L));
                    break;

                case NbtType.Float:
                    WriteSingleBE(writer, (float)(tag.Value ?? 0f));
                    break;

                case NbtType.Double:
                    WriteDoubleBE(writer, (double)(tag.Value ?? 0d));
                    break;

                case NbtType.String:
                    WriteString(writer, tag.Value as string ?? string.Empty);
                    break;

                case NbtType.ByteArray:
                {
                    var bytes = tag.Value as byte[] ?? Array.Empty<byte>();
                    WriteInt32BE(writer, bytes.Length);
                    writer.Write(bytes);
                    break;
                }

                case NbtType.IntArray:
                {
                    var values = tag.Value as int[] ?? Array.Empty<int>();
                    WriteInt32BE(writer, values.Length);
                    foreach (var value in values)
                        WriteInt32BE(writer, value);
                    break;
                }

                case NbtType.LongArray:
                {
                    var values = tag.Value as long[] ?? Array.Empty<long>();
                    WriteInt32BE(writer, values.Length);
                    foreach (var value in values)
                        WriteInt64BE(writer, value);
                    break;
                }

                case NbtType.List:
                {
                    // 空列表的元素类型写 End —— 与 Java 端 new ListTag() 的默认值一致，
                    // 写 String 之类会让游戏读到「长度为 0 但类型是字符串」的怪列表。
                    var elementType = tag.Items.Count == 0 ? NbtType.End : tag.ElementType;

                    writer.Write((byte)elementType);
                    WriteInt32BE(writer, tag.Items.Count);

                    foreach (var item in tag.Items)
                        WritePayload(writer, item);
                    break;
                }

                case NbtType.Compound:
                {
                    foreach (var item in tag.Items)
                    {
                        writer.Write((byte)item.Type);
                        WriteString(writer, item.Name);
                        WritePayload(writer, item);
                    }

                    writer.Write((byte)NbtType.End);
                    break;
                }

                default:
                    throw new InvalidOperationException($"不能写出 NBT 类型 {tag.Type}");
            }
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = JavaUtf8.Encode(value);
            if (bytes.Length > ushort.MaxValue)
                throw new InvalidOperationException("NBT 字符串超过 65535 字节");

            // ⚠️ 不能写 writer.Write((ushort)length)：BinaryWriter 是小端的，
            // 而 NBT 全篇大端。长度写反了，游戏读到的名字会变成后面几个字节的垃圾。
            writer.Write((byte)(bytes.Length >> 8));
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        private static void WriteInt16BE(BinaryWriter w, short value)
        {
            w.Write((byte)(value >> 8));
            w.Write((byte)value);
        }

        private static void WriteInt32BE(BinaryWriter w, int value)
        {
            w.Write((byte)(value >> 24));
            w.Write((byte)(value >> 16));
            w.Write((byte)(value >> 8));
            w.Write((byte)value);
        }

        private static void WriteInt64BE(BinaryWriter w, long value)
        {
            for (var i = 7; i >= 0; i--)
                w.Write((byte)(value >> (i * 8)));
        }

        private static void WriteSingleBE(BinaryWriter w, float value)
        {
            var bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            w.Write(bytes);
        }

        private static void WriteDoubleBE(BinaryWriter w, double value)
        {
            var bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            w.Write(bytes);
        }
    }

    /// <summary>
    /// Java 的「修改版 UTF-8」（<c>DataInput/DataOutput.readUTF/writeUTF</c>）。
    ///
    /// <para>
    /// 它不是标准 UTF-8，差别有两处：NUL 写成 <c>C0 80</c>；**辅助平面字符（emoji）写成
    /// CESU-8 的 6 字节**（两个三字节段各表示一个 UTF-16 代理项），而不是标准 UTF-8 的 4 字节。
    /// </para>
    ///
    /// <para>
    /// 这层不能省：玩家的服务器名里出现 emoji 时，用标准 UTF-8 写回去，Java 端读到的就是
    /// 非法编码，轻则名字变问号，重则整份 servers.dat 读取失败 —— 表现是「服务器列表空了」。
    /// </para>
    /// </summary>
    internal static class JavaUtf8
    {
        public static byte[] Encode(string value)
        {
            if (string.IsNullOrEmpty(value))
                return Array.Empty<byte>();

            var buffer = new List<byte>(value.Length * 3);

            foreach (var ch in value)
            {
                if (ch == '\0')
                {
                    buffer.Add(0xC0);
                    buffer.Add(0x80);
                }
                else if (ch < 0x80)
                {
                    buffer.Add((byte)ch);
                }
                else if (ch < 0x800)
                {
                    buffer.Add((byte)(0xC0 | (ch >> 6)));
                    buffer.Add((byte)(0x80 | (ch & 0x3F)));
                }
                else
                {
                    // 代理项也是按单字（三字节）编，这正是 CESU-8 与标准 UTF-8 的分界
                    buffer.Add((byte)(0xE0 | (ch >> 12)));
                    buffer.Add((byte)(0x80 | ((ch >> 6) & 0x3F)));
                    buffer.Add((byte)(0x80 | (ch & 0x3F)));
                }
            }

            return buffer.ToArray();
        }

        public static string Decode(ReadOnlySpan<byte> bytes)
        {
            var builder = new StringBuilder(bytes.Length);

            var i = 0;
            while (i < bytes.Length)
            {
                var b = bytes[i++];

                if (b < 0x80)
                {
                    builder.Append((char)b);
                }
                else if ((b & 0xE0) == 0xC0 && i < bytes.Length)
                {
                    var b1 = bytes[i++];
                    builder.Append((char)(((b & 0x1F) << 6) | (b1 & 0x3F)));
                }
                else if ((b & 0xF0) == 0xE0 && i + 1 < bytes.Length)
                {
                    var b1 = bytes[i++];
                    var b2 = bytes[i++];
                    builder.Append((char)(((b & 0x0F) << 12) | ((b1 & 0x3F) << 6) | (b2 & 0x3F)));
                }
                else if ((b & 0xF8) == 0xF0 && i + 2 < bytes.Length)
                {
                    // 非标准但确实存在（有人用标准 UTF-8 写过），照样解成代理对
                    var b1 = bytes[i++];
                    var b2 = bytes[i++];
                    var b3 = bytes[i++];

                    var code = ((b & 0x07) << 18) | ((b1 & 0x3F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F);
                    if (code >= 0x10000)
                    {
                        code -= 0x10000;
                        builder.Append((char)(0xD800 + (code >> 10)));
                        builder.Append((char)(0xDC00 + (code & 0x3FF)));
                    }
                    else
                    {
                        builder.Append('?');
                    }
                }
                else
                {
                    builder.Append('?');
                }
            }

            return builder.ToString();
        }
    }
}
