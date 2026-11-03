using Scada.Storage;

namespace Scada.WinForms.V5.Northbound;

public sealed class HistoryRow
{
    public required string Time { get; init; }
    public required string TagId { get; init; }
    public required double Value { get; init; }
    public required string Quality { get; init; }

    public static HistoryRow From(HistoryPoint point) => new()
    {
        Time = point.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss.fff"),
        TagId = point.TagId,
        Value = point.Value,
        Quality = point.Quality.ToString(),
    };
}



