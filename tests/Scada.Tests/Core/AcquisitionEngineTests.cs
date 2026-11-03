using Scada.Core.Acquisition;
using Scada.Core.Devices;
using Scada.Core.Demo;
using Scada.Core.Tags;

namespace Scada.Tests.Core;

public class AcquisitionEngineTests
{
    [Fact]
    public async Task Engine_polls_session_and_updates_store()
    {
        var tags = DemoPlant.CreateTagDefinitions();
        var store = new TagStore();
        store.Initialize(tags);
        var session = new FakeSession(tags);
        await using var engine = new AcquisitionEngine(
            session, tags, store, TimeSpan.FromMilliseconds(20),
            new ReconnectSettings { InitialDelayMs = 20, MaxDelayMs = 50 });

        await engine.StartAsync();
        await Task.Delay(120);
        await engine.StopAsync();

        Assert.True(engine.Stats.Cycles >= 2);
        Assert.Equal(TagQuality.Good, store.Get("level").Quality);
        Assert.True(session.OpenCount >= 1);
    }

    [Fact]
    public async Task Read_failure_marks_tags_bad_and_counts_failure()
    {
        var tags = DemoPlant.CreateTagDefinitions();
        var store = new TagStore();
        store.Initialize(tags);
        var session = new FakeSession(tags) { FailReads = true };
        await using var engine = new AcquisitionEngine(
            session, tags, store, TimeSpan.FromMilliseconds(20),
            new ReconnectSettings { InitialDelayMs = 20, MaxDelayMs = 40 });

        await engine.StartAsync();
        await Task.Delay(100);
        await engine.StopAsync();

        Assert.True(engine.Stats.Failures >= 1);
        Assert.Equal(TagQuality.Bad, store.Get("level").Quality);
        Assert.Contains("模拟", store.Get("level").Error);
    }

    private sealed class FakeSession : IDeviceSession
    {
        private readonly IReadOnlyList<TagDefinition> _tags;

        public FakeSession(IReadOnlyList<TagDefinition> tags) => _tags = tags;

        public string DeviceId => "fake";
        public DeviceConnectionState State { get; private set; } = DeviceConnectionState.Disconnected;
        public string? LastError { get; private set; }
        public int OpenCount { get; private set; }
        public bool FailReads { get; init; }

        public event Action<DeviceConnectionState>? StateChanged;

        public Task OpenAsync(CancellationToken ct = default)
        {
            OpenCount++;
            State = DeviceConnectionState.Online;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, TagValue>> ReadAsync(
            IReadOnlyList<TagDefinition> tags, CancellationToken ct = default)
        {
            if (FailReads)
            {
                LastError = "模拟读取失败";
                throw new IOException(LastError);
            }

            IReadOnlyDictionary<string, TagValue> values = _tags.ToDictionary(
                t => t.Id,
                t => new TagValue(t.Id, 42, 42, TagQuality.Good, DateTimeOffset.UtcNow));
            return Task.FromResult(values);
        }

        public Task WriteAsync(TagDefinition tag, double value, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task CloseAsync()
        {
            State = DeviceConnectionState.Disconnected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
