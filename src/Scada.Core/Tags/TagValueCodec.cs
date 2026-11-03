using System.Buffers.Binary;

namespace Scada.Core.Tags;

/// <summary>
/// 原始寄存器 &lt;-&gt; 工程值。这里故意把公式写得很直白，方便对照文档。
///
/// 小白阅读顺序：
/// 1. Decode：设备寄存器 → 工程值（界面显示的值）
/// 2. Encode：工程值 → 设备寄存器（写 PLC 时用）
/// 3. BuildBytes / FromBytes：处理 float32/int32 占两个寄存器时的字节序
///
/// 关键概念：Modbus 一个寄存器固定 16 位；float32 需要 2 个寄存器，
/// 这 4 个字节在 PLC 里的排列方式就是 ABCD/DCBA/BADC/CDAB。
/// </summary>
public static class TagValueCodec
{
    /// <summary>解码成“工程值”。例如寄存器 418、Scale=0.1 → 41.8%。</summary>
    public static double Decode(ReadOnlySpan<ushort> registers, TagDefinition tag)
    {
        var raw = DecodeRaw(registers, tag);

        // bool 不做线性缩放；只有模拟量才套用 y = kx + b。
        if (tag.DataType == TagDataType.Bool)
            return raw;

        return raw * tag.Scale + tag.Offset;
    }

    /// <summary>只解码“原始值”，不套缩放。界面上的“原始值”列就是它。</summary>
    public static double DecodeRaw(ReadOnlySpan<ushort> registers, TagDefinition tag)
    {
        // 先确认寄存器数量够不够，避免把 2 个寄存器的 float 当成 1 个寄存器读。
        EnsureRegisters(registers, tag);

        return tag.DataType switch
        {
            // 线圈/离散输入读到 0/1；保持寄存器里的 bool 通常 0=OFF，非 0=ON。
            TagDataType.Bool => registers[0] != 0 ? 1 : 0,
            TagDataType.UInt16 => registers[0],
            // ushort 是 16 位无符号；强转 short 后，0xFFFF 会变成 -1。
            TagDataType.Int16 => unchecked((short)registers[0]),
            // 下面三种都是 32 位，占 2 个寄存器，必须先按字节序拼成 4 个字节。
            TagDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(BuildBytes(registers, tag.ByteOrder)),
            TagDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(BuildBytes(registers, tag.ByteOrder)),
            TagDataType.Float32 => BinaryPrimitives.ReadSingleBigEndian(BuildBytes(registers, tag.ByteOrder)),
            _ => throw new NotSupportedException($"不支持的数据类型：{tag.DataType}"),
        };
    }

    /// <summary>把工程值编码成寄存器数组。写 PLC 时用，是 Decode 的逆运算。</summary>
    public static ushort[] Encode(double engineeringValue, TagDefinition tag)
    {
        // bool 只接受 0/1，不参与缩放。
        if (tag.DataType == TagDataType.Bool)
            return new[] { engineeringValue != 0 ? (ushort)1 : (ushort)0 };

        // Scale=0 会导致除法爆炸；配置错误要在写设备前就拦住。
        if (tag.Scale == 0)
            throw new InvalidOperationException($"点位 {tag.Id} 的缩放系数不能为 0。");

        // 逆运算：工程值 75°C、Scale=0.1 → 原始值 750。
        var raw = (engineeringValue - tag.Offset) / tag.Scale;

        return tag.DataType switch
        {
            TagDataType.UInt16 => new[] { ToUInt16(raw) },
            TagDataType.Int16 => new[] { unchecked((ushort)(short)Math.Clamp(Math.Round(raw), short.MinValue, short.MaxValue)) },
            TagDataType.UInt32 => FromBytes(ToBigEndianBytes((uint)Math.Clamp(Math.Round(raw), 0, uint.MaxValue)), tag.ByteOrder),
            TagDataType.Int32 => FromBytes(ToBigEndianBytes((int)Math.Clamp(Math.Round(raw), int.MinValue, int.MaxValue)), tag.ByteOrder),
            TagDataType.Float32 => FromBytes(ToBigEndianBytes((float)raw), tag.ByteOrder),
            _ => throw new NotSupportedException($"不支持的数据类型：{tag.DataType}"),
        };
    }

    private static ushort ToUInt16(double raw)
        => (ushort)Math.Clamp(Math.Round(raw), 0, ushort.MaxValue);

    private static byte[] ToBigEndianBytes(float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleBigEndian(bytes, value);
        return bytes;
    }

    private static byte[] ToBigEndianBytes(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] ToBigEndianBytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>把两个寄存器整理成“按当前字节序解释时的大端字节数组”。</summary>
    private static byte[] BuildBytes(ReadOnlySpan<ushort> registers, ByteOrder order)
    {
        // 一个寄存器拆成高字节/低字节。例如寄存器 0x428A → r0Hi=0x42, r0Lo=0x8A。
        byte r0Hi = (byte)(registers[0] >> 8);
        byte r0Lo = (byte)(registers[0] & 0xFF);
        byte r1Hi = (byte)(registers[1] >> 8);
        byte r1Lo = (byte)(registers[1] & 0xFF);

        // 目标数组在内存中按“大端顺序”排列，交给 BinaryPrimitives 读取。
        // 以 float 72.5（大端字节 42 91 00 00）为例：
        //   ABCD：寄存器0=0x4291，寄存器1=0x0000
        //   DCBA：寄存器0=0x0000，寄存器1=0x9142
        //   BADC：字内交换 → 0x9142 / 0x0000
        //   CDAB：字交换   → 0x0000 / 0x4291
        return order switch
        {
            ByteOrder.ABCD => new[] { r0Hi, r0Lo, r1Hi, r1Lo },
            ByteOrder.BADC => new[] { r0Lo, r0Hi, r1Lo, r1Hi },
            ByteOrder.CDAB => new[] { r1Hi, r1Lo, r0Hi, r0Lo },
            ByteOrder.DCBA => new[] { r1Lo, r1Hi, r0Lo, r0Hi },
            _ => throw new ArgumentOutOfRangeException(nameof(order)),
        };
    }

    /// <summary>大端字节数组 -&gt; 两个寄存器（按当前字节序摆放）。BuildBytes 的逆过程。</summary>
    private static ushort[] FromBytes(byte[] bigEndian, ByteOrder order)
    {
        // a/b/c/d 是“逻辑上的高字节→低字节”，分别是 0x42、0x91、0x00、0x00。
        var (a, b, c, d) = (bigEndian[0], bigEndian[1], bigEndian[2], bigEndian[3]);
        return order switch
        {
            ByteOrder.ABCD => new[] { (ushort)((a << 8) | b), (ushort)((c << 8) | d) },
            ByteOrder.BADC => new[] { (ushort)((b << 8) | a), (ushort)((d << 8) | c) },
            ByteOrder.CDAB => new[] { (ushort)((c << 8) | d), (ushort)((a << 8) | b) },
            ByteOrder.DCBA => new[] { (ushort)((d << 8) | c), (ushort)((b << 8) | a) },
            _ => throw new ArgumentOutOfRangeException(nameof(order)),
        };
    }

    private static void EnsureRegisters(ReadOnlySpan<ushort> registers, TagDefinition tag)
    {
        if (registers.Length < tag.RegisterCount)
            throw new ArgumentException($"点位 {tag.Id} 需要 {tag.RegisterCount} 个寄存器，实际只有 {registers.Length} 个。");
    }
}
