namespace Scada.Core.Tags;

/// <summary>点位的数据类型。寄存器数量由类型决定。</summary>
public enum TagDataType
{
    UInt16,
    Int16,
    UInt32,
    Int32,
    Float32,
    Bool,
}

/// <summary>32 位数据的字/字节序。Modbus 寄存器本身按大端传输。</summary>
public enum ByteOrder
{
    /// <summary>A B C D：高字在前，字内高字节在前（最常见）。</summary>
    ABCD,
    /// <summary>D C B A：完全反序。</summary>
    DCBA,
    /// <summary>B A D C：字内字节交换。</summary>
    BADC,
    /// <summary>C D A B：字交换。</summary>
    CDAB,
}

/// <summary>质量码是 SCADA 与普通曲线工具最大的区别之一：坏值绝不能伪装成 0。</summary>
public enum TagQuality
{
    Good,
    Uncertain,
    Bad,
    Stale,
}

public enum AlarmLevel
{
    None = 0,
    LL = 1,
    L = 2,
    H = 3,
    HH = 4,
}

/// <summary>一个点位的四限报警配置。所有阈值都是工程值。</summary>
public sealed class AlarmDefinition
{
    public double? LL { get; init; }
    public double? L { get; init; }
    public double? H { get; init; }
    public double? HH { get; init; }

    /// <summary>恢复回差，防止数值在阈值附近抖动导致报警刷屏。</summary>
    public double Deadband { get; init; } = 1.0;

    /// <summary>连续超过阈值多少秒才真正触发。</summary>
    public double DelaySeconds { get; init; } = 2.0;

    public bool Enabled { get; init; } = true;
}

public sealed class TagDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>协议地址，例如 HR:0、C:3、40001。</summary>
    public required string Address { get; init; }

    public TagDataType DataType { get; init; } = TagDataType.UInt16;
    public ByteOrder ByteOrder { get; init; } = ByteOrder.ABCD;

    /// <summary>工程值 = 原值 * Scale + Offset。</summary>
    public double Scale { get; init; } = 1.0;
    public double Offset { get; init; }

    public int Decimals { get; init; } = 1;
    public string Unit { get; init; } = "";
    public bool Writable { get; init; }
    public AlarmDefinition? Alarm { get; init; }

    public int RegisterCount => DataType is TagDataType.UInt32 or TagDataType.Int32 or TagDataType.Float32 ? 2 : 1;

    public bool IsBool => DataType == TagDataType.Bool;

    public string Format(double engineeringValue)
    {
        if (DataType == TagDataType.Bool)
            return engineeringValue != 0 ? "ON" : "OFF";
        return engineeringValue.ToString($"F{Decimals}") + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit);
    }
}

/// <summary>某一时刻的一个点位值；RawValue 保留设备原始值，便于排错。</summary>
public sealed record TagValue(
    string TagId,
    double Value,
    double RawValue,
    TagQuality Quality,
    DateTimeOffset Timestamp,
    string? Error = null)
{
    public bool IsGood => Quality is TagQuality.Good or TagQuality.Uncertain;

    public static TagValue Bad(string tagId, string error, TagQuality quality = TagQuality.Bad)
        => new(tagId, double.NaN, double.NaN, quality, DateTimeOffset.UtcNow, error);
}

public enum TagArea
{
    HoldingRegister,
    InputRegister,
    Coil,
    DiscreteInput,
}

/// <summary>
/// 把常见的地址写法统一成“区域 + 0 基偏移”。
/// 支持 HR:0 / C:3 / IR:0 / DI:0，以及 40001 / 00001 这类 1 基写法。
/// </summary>
public readonly record struct TagAddress(TagArea Area, int Offset)
{
    public static TagAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("点位地址不能为空，例如 HR:0 或 40001。");

        var text = address.Trim();
        if (text.Contains(':'))
        {
            var parts = text.Split(':', 2, StringSplitOptions.TrimEntries);
            var area = parts[0].ToUpperInvariant() switch
            {
                "HR" or "H" or "HOLDING" => TagArea.HoldingRegister,
                "IR" or "INPUT" => TagArea.InputRegister,
                "C" or "COIL" => TagArea.Coil,
                "DI" or "DISCRETE" => TagArea.DiscreteInput,
                _ => throw new FormatException($"不认识的地址区域：{parts[0]}"),
            };
            if (!int.TryParse(parts[1], out var offset) || offset < 0)
                throw new FormatException($"非法地址偏移：{address}");
            return new TagAddress(area, offset);
        }

        if (!int.TryParse(text, out var number) || number < 0)
            throw new FormatException($"非法地址：{address}");

        // 传统 4xxxx / 3xxxx / 0xxxx / 1xxxx 写法（1 基）。
        return number switch
        {
            >= 40001 and <= 49999 => new TagAddress(TagArea.HoldingRegister, number - 40001),
            >= 30001 and <= 39999 => new TagAddress(TagArea.InputRegister, number - 30001),
            >= 10001 and <= 19999 => new TagAddress(TagArea.DiscreteInput, number - 10001),
            >= 1 and <= 9999 => new TagAddress(TagArea.Coil, number - 1),
            _ => new TagAddress(TagArea.HoldingRegister, number),
        };
    }

    public override string ToString() => Area switch
    {
        TagArea.HoldingRegister => $"HR:{Offset}",
        TagArea.InputRegister => $"IR:{Offset}",
        TagArea.Coil => $"C:{Offset}",
        _ => $"DI:{Offset}",
    };
}
