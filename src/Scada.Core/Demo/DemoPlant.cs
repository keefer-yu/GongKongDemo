using Scada.Core.Devices;
using Scada.Core.Tags;

namespace Scada.Core.Demo;

/// <summary>
/// 全套 Demo 共用的“1# 反应釜”定义。
/// 配置文件和模拟寄存器映射都以这里为准，避免各版本各写一套。
/// </summary>
public static class DemoPlant
{
    public const string DeviceId = "reactor-1";
    public const string DeviceName = "1# 反应釜";
    public const int ModbusPort = 15020;
    public const byte ModbusUnitId = 1;

    public static TagDefinition[] CreateTagDefinitions() =>
    [
        new()
        {
            Id = "level", Name = "液位", Address = "HR:0", DataType = TagDataType.UInt16,
            Scale = 0.1, Decimals = 1, Unit = "%", Writable = false,
            Alarm = new AlarmDefinition { LL = 5, L = 15, H = 85, HH = 95, Deadband = 2, DelaySeconds = 2 },
        },
        new()
        {
            Id = "temp", Name = "温度", Address = "HR:1", DataType = TagDataType.Float32,
            Scale = 1, Decimals = 1, Unit = "°C", Writable = false,
            Alarm = new AlarmDefinition { L = 20, H = 80, HH = 95, Deadband = 2, DelaySeconds = 2 },
        },
        new()
        {
            Id = "pressure", Name = "压力", Address = "HR:3", DataType = TagDataType.UInt16,
            Scale = 0.001, Decimals = 3, Unit = "MPa", Writable = false,
            Alarm = new AlarmDefinition { H = 0.6, HH = 0.8, Deadband = 0.03, DelaySeconds = 1 },
        },
        new()
        {
            Id = "tempSetpoint", Name = "温度设定", Address = "HR:4", DataType = TagDataType.UInt16,
            Scale = 0.1, Decimals = 1, Unit = "°C", Writable = true,
        },
        new() { Id = "pump", Name = "泵运行", Address = "C:0", DataType = TagDataType.Bool, Writable = true },
        new() { Id = "heater", Name = "加热器", Address = "C:1", DataType = TagDataType.Bool, Writable = true },
        new() { Id = "valve", Name = "出口阀", Address = "C:2", DataType = TagDataType.Bool, Writable = true },
        new() { Id = "autoMode", Name = "自动模式", Address = "C:3", DataType = TagDataType.Bool, Writable = true },
    ];

    public static DeviceConfig CreateDeviceConfig() => new()
    {
        Device = new DeviceSettings
        {
            Id = DeviceId,
            Name = DeviceName,
            Protocol = "ModbusTcp",
            Host = "127.0.0.1",
            Port = ModbusPort,
            UnitId = ModbusUnitId,
            ScanIntervalMs = 500,
            Reconnect = new ReconnectSettings { InitialDelayMs = 1000, MaxDelayMs = 10000 },
            Limits = CreateLimits(),
        },
        Tags = CreateTagDefinitions().ToList(),
    };

    public static LimitSettings CreateLimits() => new()
    {
        PumpMinLevel = 10,
        HeaterMinLevel = 30,
        HeaterMaxTemp = 95,
        SetpointMin = 30,
        SetpointMax = 95,
    };

    public static TagStore CreateStore()
    {
        var tags = CreateTagDefinitions();
        var store = new TagStore();
        store.Initialize(tags);
        return store;
    }

    public static SimulatedDeviceSession CreateSimulatedSession()
    {
        var tags = CreateTagDefinitions();
        return new SimulatedDeviceSession(DeviceId, tags);
    }
}
