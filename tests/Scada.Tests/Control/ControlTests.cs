using Scada.Control;
using Scada.Core.Control;
using Scada.Core.Demo;
using Scada.Core.Devices;
using Scada.Core.Tags;
using Scada.Tests.Support;

namespace Scada.Tests.Control;

public class ControlTests
{
    private static (TagStore Store, InterlockEngine Interlocks, IReadOnlyList<TagDefinition> Tags)
        Context(double level = 50, double temp = 25, double setpoint = 60, bool auto = false)
    {
        var tags = DemoPlant.CreateTagDefinitions();
        var store = new TagStore();
        store.Initialize(tags);
        var now = DateTimeOffset.UtcNow;
        store.Update(new TagValue("level", level, level * 10, TagQuality.Good, now));
        store.Update(new TagValue("temp", temp, temp, TagQuality.Good, now));
        store.Update(new TagValue("pressure", 0.25, 250, TagQuality.Good, now));
        store.Update(new TagValue("tempSetpoint", setpoint, setpoint * 10, TagQuality.Good, now));
        store.Update(new TagValue("pump", 0, 0, TagQuality.Good, now));
        store.Update(new TagValue("heater", 0, 0, TagQuality.Good, now));
        store.Update(new TagValue("valve", 0, 0, TagQuality.Good, now));
        store.Update(new TagValue("autoMode", auto ? 1 : 0, auto ? 1 : 0, TagQuality.Good, now));

        return (store, new InterlockEngine(tags, DemoPlant.CreateLimits()), tags);
    }

    [Fact]
    public void Pump_start_allowed_when_level_is_safe()
    {
        var (store, interlocks, _) = Context(level: 50);
        var request = new CommandRequest { TagId = "pump", Value = 1, Confirmed = true };

        var checks = interlocks.Evaluate(request, store.Snapshot());

        Assert.All(checks, c => Assert.True(c.Passed));
    }

    [Fact]
    public void Pump_start_denied_at_low_level()
    {
        var (store, interlocks, _) = Context(level: 5);
        var request = new CommandRequest { TagId = "pump", Value = 1, Confirmed = true };

        var checks = interlocks.Evaluate(request, store.Snapshot());

        Assert.Contains(checks, c => !c.Passed && c.Reason!.Contains("干转"));
    }

    [Fact]
    public void Manual_heater_denied_in_auto_mode()
    {
        var (store, interlocks, _) = Context(level: 60, temp: 40, auto: true);
        var request = new CommandRequest { TagId = "heater", Value = 1, Confirmed = true };

        var checks = interlocks.Evaluate(request, store.Snapshot());

        Assert.Contains(checks, c => !c.Passed && c.RuleId == "heater-manual-mode");
    }

    [Fact]
    public void Setpoint_out_of_range_is_denied()
    {
        var (store, interlocks, _) = Context();
        var request = new CommandRequest { TagId = "tempSetpoint", Value = 200, Confirmed = true };

        var checks = interlocks.Evaluate(request, store.Snapshot());

        Assert.Contains(checks, c => !c.Passed && c.RuleId == "setpoint-range");
    }

    [Fact]
    public async Task Command_succeeds_with_readback_and_audit()
    {
        var (store, interlocks, tags) = Context(level: 50);
        await using var session = new FakeSession(tags);
        await session.OpenAsync();
        var service = new CommandService(session, interlocks, store, TimeSpan.FromMilliseconds(10));
        CommandAudit? audit = null;
        service.Audited += a => audit = a;

        var result = await service.ExecuteAsync(new CommandRequest
        {
            TagId = "pump", Value = 1, Confirmed = true,
        });

        Assert.True(result.Success);
        Assert.Equal(CommandState.Succeeded, result.State);
        Assert.Equal(1, result.ActualValue);
        Assert.NotNull(audit);
        Assert.True(audit!.Ok);
        Assert.Equal(1, session.Values["pump"]);
    }

    [Fact]
    public async Task Rejected_command_is_not_written()
    {
        var (store, interlocks, tags) = Context(level: 5);
        await using var session = new FakeSession(tags);
        await session.OpenAsync();
        var service = new CommandService(session, interlocks, store, TimeSpan.FromMilliseconds(10));

        var result = await service.ExecuteAsync(new CommandRequest
        {
            TagId = "pump", Value = 1, Confirmed = true,
        });

        Assert.False(result.Success);
        Assert.Equal(CommandState.Rejected, result.State);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task Unconfirmed_command_is_rejected()
    {
        var (store, interlocks, tags) = Context(level: 50);
        await using var session = new FakeSession(tags);
        await session.OpenAsync();
        var service = new CommandService(session, interlocks, store, TimeSpan.FromMilliseconds(10));

        var result = await service.ExecuteAsync(new CommandRequest
        {
            TagId = "pump", Value = 1, Confirmed = false,
        });

        Assert.Equal(CommandState.Rejected, result.State);
        Assert.Empty(session.Writes);
    }

    [Fact]
    public async Task Readback_mismatch_fails_the_command()
    {
        var (store, interlocks, tags) = Context(level: 50);
        await using var session = new FakeSession(tags) { IgnoreWrites = true };
        await session.OpenAsync();
        var service = new CommandService(session, interlocks, store, TimeSpan.FromMilliseconds(10));

        var result = await service.ExecuteAsync(new CommandRequest
        {
            TagId = "pump", Value = 1, Confirmed = true,
        });

        Assert.False(result.Success);
        Assert.Equal(CommandState.Failed, result.State);
        Assert.Contains("回读不一致", result.Message);
    }

    private sealed class FakeSession : IDeviceSession
    {
        private readonly IReadOnlyList<TagDefinition> _tags;

        public FakeSession(IReadOnlyList<TagDefinition> tags)
        {
            _tags = tags;
            Values = tags.ToDictionary(t => t.Id, _ => 0.0, StringComparer.OrdinalIgnoreCase);
            Values["level"] = 50;
            Values["temp"] = 25;
            Values["tempSetpoint"] = 60;
        }

        public Dictionary<string, double> Values { get; }
        public List<string> Writes { get; } = [];
        public bool IgnoreWrites { get; init; }

        public string DeviceId => "fake";
        public DeviceConnectionState State { get; private set; } = DeviceConnectionState.Disconnected;
        public string? LastError => null;

        public event Action<DeviceConnectionState>? StateChanged;

        public Task OpenAsync(CancellationToken cancellationToken = default)
        {
            State = DeviceConnectionState.Online;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, TagValue>> ReadAsync(
            IReadOnlyList<TagDefinition> tags, CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<string, TagValue> result = tags.ToDictionary(
                t => t.Id,
                t => new TagValue(t.Id, Values.GetValueOrDefault(t.Id), Values.GetValueOrDefault(t.Id),
                    TagQuality.Good, DateTimeOffset.UtcNow));
            return Task.FromResult(result);
        }

        public Task WriteAsync(TagDefinition tag, double value, CancellationToken cancellationToken = default)
        {
            Writes.Add(tag.Id);
            if (!IgnoreWrites) Values[tag.Id] = value;
            return Task.CompletedTask;
        }

        public Task CloseAsync()
        {
            State = DeviceConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
