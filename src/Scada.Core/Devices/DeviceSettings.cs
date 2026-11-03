using Scada.Core.Tags;

namespace Scada.Core.Devices;

public sealed class ReconnectSettings
{
    public int InitialDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 10000;
}

/// <summary>演示用的安全阈值，真实项目通常来自工艺配方/权限系统。</summary>
public sealed class LimitSettings
{
    public double PumpMinLevel { get; set; } = 10;
    public double HeaterMinLevel { get; set; } = 30;
    public double HeaterMaxTemp { get; set; } = 95;
    public double SetpointMin { get; set; } = 30;
    public double SetpointMax { get; set; } = 95;
}

public sealed class DeviceSettings
{
    public string Id { get; set; } = "reactor-1";
    public string Name { get; set; } = "1# 反应釜";
    public string Protocol { get; set; } = "ModbusTcp";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 15020;
    public byte UnitId { get; set; } = 1;
    public int ScanIntervalMs { get; set; } = 500;
    public int TimeoutMs { get; set; } = 2000;
    public ReconnectSettings Reconnect { get; set; } = new();
    public LimitSettings Limits { get; set; } = new();
}

public sealed class DeviceConfig
{
    public DeviceSettings Device { get; set; } = new();
    public List<TagDefinition> Tags { get; set; } = new();
}

public enum DeviceConnectionState
{
    Disconnected,
    Connecting,
    Online,
    Faulted,
}

/// <summary>
/// 驱动层统一接口。V1 用内存模拟设备，V2 起用 Modbus TCP 实现；
/// 采集引擎、命令服务只依赖这个接口。
/// </summary>
public interface IDeviceSession : IAsyncDisposable
{
    string DeviceId { get; }
    DeviceConnectionState State { get; }
    string? LastError { get; }

    event Action<DeviceConnectionState>? StateChanged;

    Task OpenAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, TagValue>> ReadAsync(
        IReadOnlyList<TagDefinition> tags, CancellationToken cancellationToken = default);
    Task WriteAsync(TagDefinition tag, double value, CancellationToken cancellationToken = default);
    Task CloseAsync();
}
