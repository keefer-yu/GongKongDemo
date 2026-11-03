using Scada.Core.Control;

namespace Scada.WinForms.V5.Northbound;

public sealed class AuditRow
{
    public required string Time { get; init; }
    public required string Source { get; init; }
    public required string Tag { get; init; }
    public required string Requested { get; init; }
    public required string Actual { get; init; }
    public required string Result { get; init; }
    public required string Message { get; init; }
    public required string Duration { get; init; }

    public static AuditRow From(CommandAudit audit) => new()
    {
        Time = audit.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
        Source = audit.Source.ToString(),
        Tag = audit.TagId,
        Requested = audit.Requested.ToString("F2"),
        Actual = audit.Actual?.ToString("F2") ?? "—",
        Result = audit.Ok ? "成功" : "失败",
        Message = audit.Message,
        Duration = $"{audit.DurationMs:F0} ms",
    };
}


