using System.Text.Json.Serialization;

namespace Scada.Core.Integration;

public sealed record TelemetryValue(
    string Id,
    string Name,
    double Value,
    string Unit,
    string Quality,
    double Raw);

public sealed record TelemetryEnvelope(
    DateTimeOffset Timestamp,
    string DeviceId,
    string DeviceName,
    long Sequence,
    IReadOnlyList<TelemetryValue> Values);

public sealed record AlarmEnvelope(
    DateTimeOffset Timestamp,
    string TagId,
    string TagName,
    string Level,
    string State,
    double Value,
    string Message);

public sealed record RemoteCommand(
    string Id,
    string TagId,
    double Value,
    string Token,
    string Source = "Mqtt");

public sealed record CommandAck(
    string Id,
    string TagId,
    bool Ok,
    string State,
    string Message,
    double? ActualValue,
    DateTimeOffset Timestamp);

/// <summary>北向 MQTT 主题约定。V5 与上位模拟器共用，避免两边各写一份字符串。</summary>
public sealed class TopicNames
{
    public TopicNames(string prefix) => Prefix = prefix.TrimEnd('/');

    public string Prefix { get; }
    public string Telemetry => $"{Prefix}/telemetry";
    public string Alarm => $"{Prefix}/alarm";
    public string Status => $"{Prefix}/status";
    public string Command => $"{Prefix}/cmd";
    public string CommandAck => $"{Prefix}/cmd/ack";

    public string All => $"{Prefix}/#";
}

public sealed record NorthboundStatus(
    bool Running,
    bool MqttConnected,
    bool RestRunning,
    string? MqttError,
    string? RestError,
    long PublishedMessages,
    long PendingMessages,
    DateTimeOffset Timestamp)
{
    [JsonIgnore]
    public bool Healthy => Running && (MqttConnected || RestRunning);
}
