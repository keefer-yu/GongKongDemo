namespace Scada.Tests.Support;

/// <summary>测试用可控时钟，避免报警延时测试真的等 2 秒。</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
