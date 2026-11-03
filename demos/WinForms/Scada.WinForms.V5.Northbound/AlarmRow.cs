using Scada.Alarms;
using Scada.Core.Tags;

namespace Scada.WinForms.V5.Northbound;

public sealed class AlarmRow
{
    public required string TagId { get; init; }
    public required string Time { get; init; }
    public required string Level { get; init; }
    public required string TagName { get; init; }
    public required string Message { get; init; }
    public required string State { get; init; }

    public static AlarmRow From(AlarmRecord record) => new()
    {
        TagId = record.TagId,
        Time = record.RaisedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"),
        Level = record.Level switch
        {
            AlarmLevel.HH => "HH 高高",
            AlarmLevel.H => "H 高",
            AlarmLevel.LL => "LL 低低",
            AlarmLevel.L => "L 低",
            _ => "—",
        },
        TagName = record.TagName,
        Message = record.Message,
        State = record.State switch
        {
            AlarmState.Active => "未确认",
            AlarmState.Acked => "已确认",
            AlarmState.Cleared => "已恢复",
            _ => "正常",
        },
    };
}



