namespace Scada.Protocol.Modbus;

public sealed class ModbusException : Exception
{
    public byte ExceptionCode { get; }

    public ModbusException(byte exceptionCode, string? message = null)
        : base(message ?? ExceptionText(exceptionCode))
        => ExceptionCode = exceptionCode;

    public static string ExceptionText(byte code) => code switch
    {
        0x01 => "非法功能码",
        0x02 => "非法数据地址",
        0x03 => "非法数据值",
        0x04 => "从站设备故障",
        0x05 => "确认（处理中）",
        0x06 => "从站设备忙",
        _ => $"Modbus 异常码 0x{code:X2}",
    };
}

/// <summary>纯 PDU 编解码，不涉及 TCP，便于单元测试和复用。</summary>
public static class ModbusPdu
{
    public const byte ReadCoils = 0x01;
    public const byte ReadDiscreteInputs = 0x02;
    public const byte ReadHoldingRegisters = 0x03;
    public const byte ReadInputRegisters = 0x04;
    public const byte WriteSingleCoil = 0x05;
    public const byte WriteSingleRegister = 0x06;
    public const byte WriteMultipleCoils = 0x0F;
    public const byte WriteMultipleRegisters = 0x10;

    public static byte[] BuildReadRequest(byte function, ushort start, ushort quantity)
    {
        if (function is not (ReadCoils or ReadDiscreteInputs or ReadHoldingRegisters or ReadInputRegisters))
            throw new ArgumentOutOfRangeException(nameof(function), "不是读功能码。");

        return
        [
            function,
            (byte)(start >> 8), (byte)start,
            (byte)(quantity >> 8), (byte)quantity,
        ];
    }

    public static byte[] BuildWriteSingleCoil(ushort address, bool value) =>
    [
        WriteSingleCoil,
        (byte)(address >> 8), (byte)address,
        value ? (byte)0xFF : (byte)0x00,
        (byte)0x00,
    ];

    public static byte[] BuildWriteSingleRegister(ushort address, ushort value) =>
    [
        WriteSingleRegister,
        (byte)(address >> 8), (byte)address,
        (byte)(value >> 8), (byte)value,
    ];

    public static byte[] BuildWriteMultipleRegisters(ushort start, ReadOnlySpan<ushort> values)
    {
        if (values.Length is < 1 or > 123)
            throw new ArgumentOutOfRangeException(nameof(values), "一次写寄存器数量必须为 1..123。");

        var pdu = new byte[6 + values.Length * 2];
        pdu[0] = WriteMultipleRegisters;
        pdu[1] = (byte)(start >> 8);
        pdu[2] = (byte)start;
        pdu[3] = (byte)(values.Length >> 8);
        pdu[4] = (byte)values.Length;
        pdu[5] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++)
        {
            pdu[6 + i * 2] = (byte)(values[i] >> 8);
            pdu[7 + i * 2] = (byte)values[i];
        }
        return pdu;
    }

    public static bool[] ParseBitResponse(ReadOnlySpan<byte> pdu, byte expectedFunction, int quantity)
    {
        EnsureFunction(pdu, expectedFunction);
        if (pdu.Length < 2) throw new ModbusException(0x03, "读位响应缺少字节数。");

        var byteCount = pdu[1];
        if (pdu.Length < 2 + byteCount)
            throw new ModbusException(0x03, "读位响应长度不足。");

        var values = new bool[quantity];
        for (var i = 0; i < quantity; i++)
            values[i] = (pdu[2 + i / 8] & (1 << (i % 8))) != 0;
        return values;
    }

    public static ushort[] ParseRegisterResponse(ReadOnlySpan<byte> pdu, byte expectedFunction, int quantity)
    {
        EnsureFunction(pdu, expectedFunction);
        if (pdu.Length < 2) throw new ModbusException(0x03, "读寄存器响应缺少字节数。");

        var byteCount = pdu[1];
        if (byteCount != quantity * 2 || pdu.Length < 2 + byteCount)
            throw new ModbusException(0x03, "读寄存器响应字节数不匹配。");

        var values = new ushort[quantity];
        for (var i = 0; i < quantity; i++)
            values[i] = (ushort)((pdu[2 + i * 2] << 8) | pdu[3 + i * 2]);
        return values;
    }

    public static void EnsureFunction(ReadOnlySpan<byte> pdu, byte expectedFunction)
    {
        if (pdu.Length < 2) throw new ModbusException(0x03, "PDU 过短。");

        if ((pdu[0] & 0x80) != 0)
            throw new ModbusException(pdu[1]);

        if (pdu[0] != expectedFunction)
            throw new ModbusException(0x01, $"功能码不匹配：期望 {expectedFunction}，实际 {pdu[0]}。");
    }

    public static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);
}
