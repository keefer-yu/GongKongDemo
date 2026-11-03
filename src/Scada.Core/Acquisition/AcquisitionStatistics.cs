namespace Scada.Core.Acquisition;

public sealed record AcquisitionStatistics(
    long Cycles,
    long Successes,
    long Failures,
    int ConsecutiveFailures,
    double LastLatencyMs,
    double AverageLatencyMs,
    DateTimeOffset? LastCycleAt,
    string? LastError,
    bool IsRunning)
{
    public double SuccessRate => Cycles == 0 ? 0 : Successes * 100.0 / Cycles;
}
