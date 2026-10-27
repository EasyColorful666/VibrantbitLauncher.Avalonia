using System.Text;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 大端序读取器（NBT 是大端，而 <see cref="BinaryReader"/> 是小端）。
///
/// <para>
/// 从 <see cref="WorldSeedReader"/> 里提出来共用：读 <c>level.dat</c> 和读区块 <c>.mca</c>
/// 都要按大端解析 NBT，同一个格式没必要写两份。
/// </para>
/// </summary>
internal sealed class BigEndianReader : IDisposable
{
    /// <summary>单个字符串字段的长度上限（防止把整个文件读成「一个超长字符串」）。</summary>
    private const int MaxStringLength = 1024 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _numeric = new byte[8];

    public BigEndianReader(Stream stream) => _stream = stream;

    public byte ReadByte()
    {
        Require(1);
        return _numeric[0];
    }

    public short ReadInt16()
    {
        Require(2);
        return (short)((_numeric[0] << 8) | _numeric[1]);
    }

    public int ReadInt32()
    {
        Require(4);
        return (_numeric[0] << 24) | (_numeric[1] << 16) | (_numeric[2] << 8) | _numeric[3];
    }

    public long ReadInt64()
    {
        Require(8);

        var value = 0L;
        for (var i = 0; i < 8; i++)
            value = (value << 8) | _numeric[i];

        return value;
    }

    public float ReadSingle()
    {
        Require(4);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(_numeric, 0, 4);

        return BitConverter.ToSingle(_numeric, 0);
    }

    public double ReadDouble()
    {
        Require(8);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(_numeric, 0, 8);

        return BitConverter.ToDouble(_numeric, 0);
    }

    public string ReadString()
    {
        var length = (int)(ushort)ReadInt16();
        if (length == 0)
            return string.Empty;

        if (length > MaxStringLength)
            throw new InvalidDataException("NBT 字符串长度超出上限");

        var bytes = new byte[length];
        if (!TryReadExact(bytes))
            throw new EndOfStreamException("NBT 数据意外结束");

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>丢弃若干个字节（数组载荷用不上，但不能不读 —— 否则后面的字段全部错位）。</summary>
    public void Skip(int count)
    {
        if (count <= 0)
            return;

        var buffer = new byte[Math.Min(count, 8192)];
        var remaining = count;

        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, buffer.Length);
            if (!TryReadExact(buffer.AsSpan(0, chunk)))
                throw new EndOfStreamException("NBT 数据意外结束");

            remaining -= chunk;
        }
    }

    private void Require(int count)
    {
        if (!TryReadExact(_numeric.AsSpan(0, count)))
            throw new EndOfStreamException("NBT 数据意外结束");
    }

    private bool TryReadExact(Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = _stream.Read(buffer[read..]);
            if (n <= 0)
                return false;

            read += n;
        }

        return true;
    }

    public void Dispose() => _stream.Dispose();
}
