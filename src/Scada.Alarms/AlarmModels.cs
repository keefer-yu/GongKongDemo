using Scada.Core.Tags;

namespace Scada.Alarms;

public enum AlarmState
{
    Normal,
    Active,
    Acked,
    Cleared,
}

public sealed class AlarmRecord
{
    public required string TagId { get; init; }
    public required string TagName { get; init; }
    public AlarmLevel Level { get; set; }
    public AlarmState State { get; set; } = AlarmState.Active;
    public double Value { get; set; }
    public string Message { get; set; } = "";
    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }

    public bool IsActive => State is AlarmState.Active or AlarmState.Acked;
}
