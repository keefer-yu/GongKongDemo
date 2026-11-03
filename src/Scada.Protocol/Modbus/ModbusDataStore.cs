namespace Scada.Protocol.Modbus;

/// <summary>从站数据区抽象。真实设备由 PLC 内存实现，演示由工艺模型实现。</summary>
public interface IModbusDataStore
{
    ushort[] ReadHoldingRegisters(int start, int count);
    ushort[] ReadInputRegisters(int start, int count);
    bool[] ReadCoils(int start, int count);
    bool[] ReadDiscreteInputs(int start, int count);

    void WriteSingleCoil(int address, bool value);
    void WriteSingleRegister(int address, ushort value);
    void WriteMultipleRegisters(int start, ReadOnlySpan<ushort> values);
}

/// <summary>测试和最小演示用的内存数据区。</summary>
public sealed class MemoryModbusDataStore : IModbusDataStore
{
    private readonly ushort[] _holding = new ushort[65536];
    private readonly ushort[] _input = new ushort[65536];
    private readonly bool[] _coils = new bool[65536];
    private readonly bool[] _discrete = new bool[65536];

    public ushort[] Holding => _holding;
    public ushort[] Input => _input;
    public bool[] Coils => _coils;
    public bool[] DiscreteInputs => _discrete;

    public ushort[] ReadHoldingRegisters(int start, int count) => Slice(_holding, start, count);
    public ushort[] ReadInputRegisters(int start, int count) => Slice(_input, start, count);
    public bool[] ReadCoils(int start, int count) => Slice(_coils, start, count);
    public bool[] ReadDiscreteInputs(int start, int count) => Slice(_discrete, start, count);

    public void WriteSingleCoil(int address, bool value)
    {
        Validate(address, 1);
        _coils[address] = value;
    }

    public void WriteSingleRegister(int address, ushort value)
    {
        Validate(address, 1);
        _holding[address] = value;
    }

    public void WriteMultipleRegisters(int start, ReadOnlySpan<ushort> values)
    {
        Validate(start, values.Length);
        values.CopyTo(_holding.AsSpan(start));
    }

    private static T[] Slice<T>(T[] source, int start, int count)
    {
        Validate(start, count);
        return source[start..(start + count)];
    }

    private static void Validate(int start, int count)
    {
        if (start < 0 || count < 0 || start + count > 65536)
            throw new ArgumentOutOfRangeException(nameof(start), "寄存器/线圈地址越界。");
    }
}
