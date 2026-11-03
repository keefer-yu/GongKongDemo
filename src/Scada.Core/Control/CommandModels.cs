namespace Scada.Core.Control;

public enum CommandSource
{
    Local,
    Rest,
    Mqtt,
}

public enum CommandState
{
    Pending,
    Validating,
    Writing,
    Verifying,
    Succeeded,
    Failed,
    Rejected,
    Timeout,
}

public sealed class CommandRequest
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string TagId { get; init; }
    public double Value { get; init; }
    public CommandSource Source { get; init; } = CommandSource.Local;

    /// <summary>关键写操作必须由操作员确认；远程调用由北向适配器在验签后置为 true。</summary>
    public bool Confirmed { get; init; }

    public string? Token { get; init; }
    public string? Origin { get; init; }
}

public sealed record InterlockCheck(
    string RuleId,
    string Description,
    bool Passed,
    string? Reason)
{
    public static InterlockCheck Ok(string id, string description)
        => new(id, description, true, null);

    public static InterlockCheck Fail(string id, string description, string reason)
        => new(id, description, false, reason);
}

public sealed record CommandResult(
    string CommandId,
    string TagId,
    bool Success,
    CommandState State,
    double RequestedValue,
    double? ActualValue,
    string Message,
    IReadOnlyList<InterlockCheck> InterlockChecks,
    DateTimeOffset StartedAt,
    TimeSpan Duration);

public sealed record CommandAudit(
    DateTimeOffset Timestamp,
    CommandSource Source,
    string TagId,
    double Requested,
    double? Actual,
    bool Ok,
    string Message,
    double DurationMs);
