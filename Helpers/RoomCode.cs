using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace VibrantbitLauncher.Helpers;

/// <summary>
/// 联机码：<c>U/ABCD-EFGH-JKLM-NPQR</c>。
///
/// <para>
/// 本体是 16 位「base-34」数字（字符集 <c>0-9A-Z</c> 去掉容易看错的 I 与 O），
/// 以 4-4-4-4 分组加连字符，前面带 <c>U/</c> 前缀。手抄一份就能换台电脑，
/// 比一长串随机密钥好念也好核。
/// </para>
///
/// <para>
/// 它把一次组网要用的两样东西压进同一串字符：
/// </para>
/// <list type="bullet">
/// <item>前 8 位 → 虚拟网络名 <c>scaffolding-mc-XXXX-XXXX</c></item>
/// <item>后 8 位 → 虚拟网络密码 <c>XXXX-XXXX</c></item>
/// </list>
///
/// <para>
/// <b>联机码与陶瓦联机（Terracotta）通用</b>：字符集、位序、校验、派生规则完全照搬它的算法，
/// 所以同一串码在两边解出来的网络凭据是同一份。默认中继已改为本项目自己的节点，
/// 但备用清单里仍然保留陶瓦的节点 —— 只要房间还挂在陶瓦的节点上，陶瓦客户端就照样能进。
/// </para>
///
/// <para>
/// 网名里的 <c>scaffolding-mc-</c> 不是随便起的前缀，是双方约定的固定串，改了就通不了。
/// 它跟「陶瓦联机服务」无关 —— 我们跑的仍然是自己随包下载的 EasyTier 核心，
/// 只是用同一套网络凭据，好让两个客户端的人能进同一个房间。
/// </para>
///
/// <para>
/// 校验位：合法码的数值必须能被 7 整除。手抄错一位几乎都会被这条挡下来，
/// 不会让人在错误的房间里干等半小时。
/// </para>
/// </summary>
public sealed class RoomCode
{
    /// <summary>字符集：0-9 + A-Z，去掉 I 与 O（手抄时最容易混）。</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>有效字符位数（不含前缀与连字符）。</summary>
    private const int DigitCount = 16;

    /// <summary>含前缀与连字符的完整长度：<c>U/</c> + 16 位 + 3 个 <c>-</c>。</summary>
    private const int CodeLength = 2 + DigitCount + 3;

    /// <summary>取模的基数，等于字符集大小。</summary>
    private const int Radix = 34;

    /// <summary>校验除数：合法码的数值必须能被它整除。</summary>
    private const int ChecksumDivisor = 7;

    /// <summary>
    /// 网络名固定前缀。这是与陶瓦的约定，**不能改** ——
    /// 改了之后码看起来一样，实际却是两张不同的网，谁也见不到谁。
    /// </summary>
    private const string NetworkNamePrefix = "scaffolding-mc-";

    /// <summary>可表示的空间 34^16 ≈ 3.19e24；超过 <see cref="ulong"/> 上限，只能用 <see cref="UInt128"/>。</summary>
    private static readonly UInt128 Space = ComputeSpace();

    /// <summary>本项目的直连中继节点 —— 不勾「陶瓦兼容模式」时用它。</summary>
    public const string OwnRelay = "tcp://easytier.weiai.org.cn:11010";

    /// <summary>陶瓦联机的默认节点 —— 勾了「陶瓦兼容模式」时用它。</summary>
    public const string TerracottaRelay = "https://etnode.zkitefly.eu.org/node1";

    /// <summary>页面上「中继服务器」输入框的默认值：本项目的节点。</summary>
    public const string DefaultRelay = OwnRelay;

    /// <summary>
    /// 自家节点池。第一条是主节点，其余是同批备用（核心会依次尝试）。
    ///
    /// <para>
    /// 备用是 EasyTier 官方的两个公共节点：自家节点临时抽风时，房主和房客会
    /// 一起落到同一个备用节点上，房间不至于直接组不起来。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> OwnRelays = new[]
    {
        OwnRelay,
        "tcp://public.easytier.top:11010",
        "tcp://public2.easytier.cn:54321"
    };

    /// <summary>
    /// 陶瓦联机的节点池。第一条与 <see cref="TerracottaRelay"/> 相同。
    ///
    /// <para>
    /// 要跟陶瓦客户端的人进同一张网，双方得连在同一个节点池里 ——
    /// 网名密码对上还不够。陶瓦的节点填的是「发地址的接口」，
    /// 启动前会先去问一次真正的服务器地址（见 <c>PublicRelayResolver</c>）。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> TerracottaRelays = new[]
    {
        TerracottaRelay,
        "https://etnode.zkitefly.eu.org/node2",
        "tcp://public.easytier.top:11010",
        "tcp://public2.easytier.cn:54321"
    };

    private RoomCode(string code, string digits, UInt128 value)
    {
        Code = code;
        Value = value;

        // 与陶瓦的派生规则逐字一致：前 8 位做网名（4-4 分组），后 8 位做密码（4-4 分组），
        // 码本身是大写，派生出来的凭据也保持大写 —— 大小写敏感的是网络凭据，不是给人看的码。
        NetworkName = NetworkNamePrefix
                      + digits.Substring(0, 4)
                      + "-"
                      + digits.Substring(4, 4);

        NetworkSecret = digits.Substring(8, 4)
                        + "-"
                        + digits.Substring(12, 4);
    }

    /// <summary>规范化后的联机码（大写、带连字符），可以直接发给别人。</summary>
    public string Code { get; }

    /// <summary>解出来的虚拟网络名，形如 <c>scaffolding-mc-3K7M-9PQR</c>。</summary>
    public string NetworkName { get; }

    /// <summary>解出来的虚拟网络密码，形如 <c>2WXY-8HJK</c>。</summary>
    public string NetworkSecret { get; }

    /// <summary>联机码背后的原始数值，只用于日志排查。</summary>
    public UInt128 Value { get; }

    /// <summary>
    /// 主中继是我们认识的节点时，把同池的其余节点一并挂上做备用。
    ///
    /// 只连一个节点时，只要那个节点当时抽风，两边就组不上网；多挂几个能让核心自己换。
    /// 玩家自己填了别处（自建节点）时返回 null —— 那是他的选择，不该被我们塞几个节点进去。
    /// </summary>
    public static IReadOnlyList<string>? ExtraRelaysFor(string? primary)
    {
        var value = NormalizeRelay(primary);

        foreach (var pool in new[] { OwnRelays, TerracottaRelays })
        {
            if (!string.Equals(pool[0], value, StringComparison.OrdinalIgnoreCase))
                continue;

            var extras = new List<string>(pool.Count);
            for (var i = 1; i < pool.Count; i++)
                extras.Add(pool[i]);

            return extras;
        }

        return null;
    }

    /// <summary>是不是我们认识的节点（含省略 <c>tcp://</c> 的写法）。</summary>
    public static bool IsKnownRelay(string? relay)
    {
        var value = NormalizeRelay(relay);

        foreach (var pool in new[] { OwnRelays, TerracottaRelays })
        {
            foreach (var known in pool)
            {
                if (string.Equals(known, value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>补上缺省的协议前缀，便于比较。</summary>
    private static string NormalizeRelay(string? relay)
    {
        var value = (relay ?? string.Empty).Trim();
        if (value.Length == 0)
            return DefaultRelay;

        return value.Contains("://", StringComparison.Ordinal) ? value : "tcp://" + value;
    }

    /// <summary>
    /// 随机造一个新联机码。
    ///
    /// 先取 128 位随机数折进可表示空间，再把余数抹掉使其满足整除校验 ——
    /// 这样生成出来的码和「解出来的码」走的是同一条校验，不会自己发出一个自己都不认的码。
    /// </summary>
    public static string Generate()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);

        var value = (UInt128)0;
        foreach (var b in buffer)
            value = (value << 8) | b;

        value %= Space;
        value -= value % ChecksumDivisor;

        return FromValue(value).Code;
    }

    /// <summary>
    /// 从任意文本里解析联机码。
    ///
    /// 玩家通常是把「联机码：U/XXXX-…」整句粘进来，或者从聊天软件里复制（夹带换行、全角空格），
    /// 所以这里不要求输入纯净：先统一大写、把 I/O 纠正成 1/0、去掉空白，再在文本里滑动找码。
    /// 连字符漏掉也认（有些客户端复制出来就是光秃秃一串）。
    /// </summary>
    public static bool TryParse(string? text, out RoomCode? code, out string? error)
    {
        code = null;
        error = null;

        var normalized = Normalize(text);
        if (normalized.Length < 2 + DigitCount)
        {
            error = "联机码太短了。完整格式是 U/ 加 16 位字符，例如 U/ABCD-EFGH-JKLM-NPQR。";
            return false;
        }

        // ① 标准写法：U/ + XXXX-XXXX-XXXX-XXXX
        for (var start = 0; start + CodeLength <= normalized.Length; start++)
        {
            if (!HasPrefix(normalized, start))
                continue;

            if (!TryReadGrouped(normalized.AsSpan(start + 2, DigitCount + 3), out var value))
                continue;

            code = FromValue(value);
            return true;
        }

        // ② 没有连字符的写法：U/XXXXXXXXXXXXXXXX
        var compact = StripHyphens(normalized);
        for (var start = 0; start + 2 + DigitCount <= compact.Length; start++)
        {
            if (!HasPrefix(compact, start))
                continue;

            if (!TryReadDigits(compact.AsSpan(start + 2, DigitCount), out var value))
                continue;

            code = FromValue(value);
            return true;
        }

        // 能滑到一个 U/ 就说明「像码但没通过校验」，提示要说清是核对失败，而不是格式不认识
        error = normalized.Contains("U/", StringComparison.Ordinal)
            ? "联机码核对不通过，可能抄错了一位。请让对方重新发一次，或直接从聊天记录里复制。"
            : "没找到联机码（应当以 U/ 开头）。如果对方给的是旧版启动器的密钥，请让对方升级后用新联机码开房。";
        return false;
    }

    private static bool HasPrefix(string text, int offset)
        => text[offset] == 'U' && text[offset + 1] == '/';

    /// <summary>只做「看得见的规范化」：去空白、转大写、纠正 I/O。</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var raw in text.Trim())
        {
            if (char.IsWhiteSpace(raw))
                continue;

            var c = char.ToUpperInvariant(raw);

            // 这两个字母不在字符集里，出现就一定是被看错了（1 与 l、0 与 O）
            if (c == 'I')
                c = '1';
            else if (c == 'O')
                c = '0';

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>去掉所有连字符，得到 <c>U/XXXXXXXXXXXXXXXX</c> 这种紧凑形式。</summary>
    private static string StripHyphens(string text)
        => text.IndexOf('-') < 0 ? text : text.Replace("-", string.Empty);

    /// <summary>读 <c>XXXX-XXXX-XXXX-XXXX</c> 这 19 位；连字符位置不对直接算失败。</summary>
    private static bool TryReadGrouped(ReadOnlySpan<char> grouped, out UInt128 value)
    {
        value = 0;

        Span<char> digits = stackalloc char[DigitCount];
        var written = 0;

        for (var i = 0; i < grouped.Length; i++)
        {
            if (i == 4 || i == 9 || i == 14)
            {
                if (grouped[i] != '-')
                    return false;

                continue;
            }

            digits[written++] = grouped[i];
        }

        return written == DigitCount && TryReadDigits(digits, out value);
    }

    /// <summary>把 16 位 base-34 字符折成数值，并核对整除校验。</summary>
    private static bool TryReadDigits(ReadOnlySpan<char> digits, out UInt128 value)
    {
        value = 0;

        if (digits.Length != DigitCount)
            return false;

        var scale = (UInt128)1;
        foreach (var c in digits)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0)
                return false;

            // 最左边那一位是最低位（生成时是「先取模再除」），所以这里按位加权
            value += (UInt128)index * scale;
            scale *= Radix;
        }

        return value % ChecksumDivisor == 0;
    }

    /// <summary>把数值还原成联机码与两份网络凭据。</summary>
    private static RoomCode FromValue(UInt128 value)
    {
        Span<char> digits = stackalloc char[DigitCount];
        var rest = value;

        for (var i = 0; i < DigitCount; i++)
        {
            digits[i] = Alphabet[(int)(rest % Radix)];
            rest /= Radix;
        }

        var code = new StringBuilder(CodeLength);
        code.Append("U/");
        for (var i = 0; i < DigitCount; i++)
        {
            if (i == 4 || i == 8 || i == 12)
                code.Append('-');

            code.Append(digits[i]);
        }

        return new RoomCode(code.ToString(), new string(digits), value);
    }

    private static UInt128 ComputeSpace()
    {
        var space = (UInt128)1;
        for (var i = 0; i < DigitCount; i++)
            space *= Radix;

        return space;
    }
}
