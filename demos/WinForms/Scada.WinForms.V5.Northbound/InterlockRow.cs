using Scada.Core.Control;

namespace Scada.WinForms.V5.Northbound;

public sealed class InterlockRow
{
    public required string Rule { get; init; }
    public required string Requirement { get; init; }
    public required string State { get; init; }
    public required string Reason { get; init; }

    public static InterlockRow From(InterlockCheck check) => new()
    {
        Rule = check.RuleId,
        Requirement = check.Description,
        State = check.Passed ? "允许" : "拒绝",
        Reason = check.Reason ?? "—",
    };
}


