using Scada.Alarms;
using Scada.Core.Tags;
using Scada.Tests.Support;

namespace Scada.Tests.Alarms;

public class AlarmEngineTests
{
    private static TagDefinition TempTag() => new()
    {
        Id = "temp", Name = "温度", Address = "HR:1", DataType = TagDataType.Float32, Unit = "°C",
        Alarm = new AlarmDefinition { HH = 95, H = 80, L = 20, LL = 5, Deadband = 2, DelaySeconds = 2 },
    };

    private static Dictionary<string, TagValue> Value(double value) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["temp"] = new TagValue("temp", value, value, TagQuality.Good, DateTimeOffset.UtcNow),
    };

    [Fact]
    public void High_high_alarm_waits_for_delay_then_raises()
    {
        var time = new ManualTimeProvider();
        var engine = new AlarmEngine(new[] { TempTag() }, time);

        engine.Evaluate(Value(100));
        Assert.Empty(engine.Active);

        time.Advance(TimeSpan.FromSeconds(1));
        engine.Evaluate(Value(100));
        Assert.Empty(engine.Active);

        time.Advance(TimeSpan.FromSeconds(1));
        engine.Evaluate(Value(100));

        var alarm = Assert.Single(engine.Active);
        Assert.Equal(AlarmLevel.HH, alarm.Level);
        Assert.Equal(AlarmState.Active, alarm.State);
        Assert.Contains("高高", alarm.Message);
    }

    [Fact]
    public void Value_returning_to_normal_before_delay_cancels_alarm()
    {
        var time = new ManualTimeProvider();
        var engine = new AlarmEngine(new[] { TempTag() }, time);

        engine.Evaluate(Value(100));
        time.Advance(TimeSpan.FromSeconds(1));
        engine.Evaluate(Value(50));
        time.Advance(TimeSpan.FromSeconds(5));
        engine.Evaluate(Value(50));

        Assert.Empty(engine.Active);
    }

    [Fact]
    public void Acknowledge_changes_state_and_emits_event()
    {
        var time = new ManualTimeProvider();
        var engine = new AlarmEngine(new[] { TempTag() }, time);
        var raised = 0;
        var acked = 0;
        engine.Raised += _ => raised++;
        engine.Acknowledged += _ => acked++;

        engine.Evaluate(Value(100));
        time.Advance(TimeSpan.FromSeconds(2));
        engine.Evaluate(Value(100));

        Assert.True(engine.Acknowledge("temp"));
        Assert.Equal(1, raised);
        Assert.Equal(1, acked);
        Assert.Equal(AlarmState.Acked, Assert.Single(engine.Active).State);
    }

    [Fact]
    public void Deadband_keeps_alarm_active_until_value_leaves_band()
    {
        var time = new ManualTimeProvider();
        var engine = new AlarmEngine(new[] { TempTag() }, time);

        engine.Evaluate(Value(85));
        time.Advance(TimeSpan.FromSeconds(2));
        engine.Evaluate(Value(85));
        Assert.Equal(AlarmLevel.H, Assert.Single(engine.Active).Level);

        engine.Evaluate(Value(79));
        Assert.Equal(AlarmState.Active, Assert.Single(engine.Active).State);

        engine.Evaluate(Value(70));
        Assert.Empty(engine.Active);
        Assert.Equal(AlarmState.Cleared, engine.Recent[^1].State);
    }

    [Fact]
    public void Low_and_low_low_are_detected()
    {
        var time = new ManualTimeProvider();
        var engine = new AlarmEngine(new[] { TempTag() }, time);

        engine.Evaluate(Value(10));
        time.Advance(TimeSpan.FromSeconds(2));
        engine.Evaluate(Value(10));
        Assert.Equal(AlarmLevel.L, Assert.Single(engine.Active).Level);

        time.Advance(TimeSpan.FromSeconds(1));
        engine.Evaluate(Value(3));
        Assert.Equal(AlarmLevel.LL, Assert.Single(engine.Active).Level);
    }

    [Fact]
    public void Bad_quality_does_not_clear_an_active_alarm()
    {
        var time = new ManualTimeProvider();
        var engine = new AlarmEngine(new[] { TempTag() }, time);
        engine.Evaluate(Value(100));
        time.Advance(TimeSpan.FromSeconds(2));
        engine.Evaluate(Value(100));

        engine.Evaluate(new Dictionary<string, TagValue>
        {
            ["temp"] = TagValue.Bad("temp", "通信中断"),
        });

        Assert.Single(engine.Active);
    }
}
