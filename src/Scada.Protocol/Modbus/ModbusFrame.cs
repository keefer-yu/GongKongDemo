namespace Scada.Protocol.Modbus;

public enum ModbusDirection
{
    Sent,
    Received,
}

public sealed record ModbusFrame(
    DateTimeOffset Timestamp,
    ModbusDirection Direction,
    byte[] Adu,
    string Summary)
{
    public string Hex => Convert.ToHexString(Adu).Chunk(2).Aggregate(
        new System.Text.StringBuilder(),
        (builder, pair) => builder.Append(' ').Append(pair))
        .ToString().Trim();
}

public static class ModbusFrameFormatter
{
    public static string Describe(byte[] adu)
    {
        if (adu.Length < 8) return "报文过短";

        var transaction = (adu[0] << 8) | adu[1];
        var unit = adu[6];
        var function = adu[7];
        var isException = (function & 0x80) != 0;
        var baseFunction = (byte)(function & 0x7F);

        if (isException && adu.Length >= 9)
            return $"TID={transaction} Unit={unit} FC={baseFunction} 异常码={adu[8]}";

        return baseFunction switch
        {
            0x01 or 0x02 => adu.Length == 12
                ? $"TID={transaction} Unit={unit} FC={baseFunction:X2} 读位 起始={ReadU16(adu, 8)} 数量={ReadU16(adu, 10)}"
                : $"TID={transaction} Unit={unit} FC={baseFunction:X2} 读位响应 字节数={adu[8]}",
            0x03 or 0x04 => adu.Length == 12
                ? $"TID={transaction} Unit={unit} FC={baseFunction:X2} 读寄存器 起始={ReadU16(adu, 8)} 数量={ReadU16(adu, 10)}"
                : $"TID={transaction} Unit={unit} FC={baseFunction:X2} 读寄存器响应 字节数={adu[8]}",
            0x05 => $"TID={transaction} Unit={unit} FC=05 写单线圈 地址={ReadU16(adu, 8)} 值=0x{ReadU16(adu, 10):X4}",
            0x06 => $"TID={transaction} Unit={unit} FC=06 写单寄存器 地址={ReadU16(adu, 8)} 值={ReadU16(adu, 10)}",
            0x0F => $"TID={transaction} Unit={unit} FC=15 写多线圈 起始={ReadU16(adu, 8)} 数量={ReadU16(adu, 10)}",
            0x10 => $"TID={transaction} Unit={unit} FC=16 写多寄存器 起始={ReadU16(adu, 8)} 数量={ReadU16(adu, 10)}",
            _ => $"TID={transaction} Unit={unit} FC={baseFunction}",
        };
    }

    private static ushort ReadU16(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);
}
