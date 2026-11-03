using Scada.Alarms;
using Scada.Core.Control;
using Scada.Core.Tags;
using Scada.Storage;

namespace Scada.Tests.Storage;

public class SqliteHistoryStoreTests
{
    private static string TempDbPath()
        => Path.Combine(Path.GetTempPath(), $"scada-test-{Guid.NewGuid():N}.db");

    private static void Cleanup(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            if (!File.Exists(file)) continue;
            try { File.Delete(file); }
            catch (IOException) { /* Windows 上偶尔还会短暂占用，测试不因清理失败而失败 */ }
        }
    }

    [Fact]
    public async Task Writes_and_queries_tag_history()
    {
        var path = TempDbPath();
        try
        {
            await using var store = new SqliteHistoryStore(path);
            await store.InitializeAsync();
            var start = DateTimeOffset.UtcNow.AddMinutes(-1);

            await store.WriteTagAsync(new TagValue("temp", 72.5, 72.5, TagQuality.Good, start));
            await store.WriteTagAsync(new TagValue("temp", 73.5, 73.5, TagQuality.Good, start.AddSeconds(1)));

            var rows = await store.QueryHistoryAsync("temp", start.AddSeconds(-1), start.AddSeconds(2));
            Assert.Equal(2, rows.Count);
            Assert.Equal(72.5, rows[0].Value, 3);
            Assert.Equal(73.5, rows[1].Value, 3);
            Assert.Equal(2, await store.CountHistoryAsync());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Writes_and_queries_alarm_events()
    {
        var path = TempDbPath();
        try
        {
            await using var store = new SqliteHistoryStore(path);
            await store.InitializeAsync();

            var alarm = new AlarmRecord
            {
                TagId = "temp",
                TagName = "温度",
                Level = AlarmLevel.HH,
                State = AlarmState.Active,
                Value = 100,
                Message = "温度高高报警：100.0 °C",
                RaisedAt = DateTimeOffset.UtcNow,
            };
            await store.WriteAlarmAsync(alarm);

            var rows = await store.QueryAlarmsAsync();
            var row = Assert.Single(rows);
            Assert.Equal("temp", row.TagId);
            Assert.Equal(AlarmLevel.HH, row.Level);
            Assert.Equal(AlarmState.Active, row.State);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Writes_and_queries_command_audit()
    {
        var path = TempDbPath();
        try
        {
            await using var store = new SqliteHistoryStore(path);
            await store.InitializeAsync();

            await store.WriteAuditAsync(new CommandAudit(
                DateTimeOffset.UtcNow, CommandSource.Local, "pump", 1, 1, true, "成功", 123));

            var rows = await store.QueryAuditAsync();
            var row = Assert.Single(rows);
            Assert.Equal("pump", row.TagId);
            Assert.True(row.Ok);
            Assert.Equal(123, row.DurationMs, 3);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Prune_removes_old_rows()
    {
        var path = TempDbPath();
        try
        {
            await using var store = new SqliteHistoryStore(path);
            await store.InitializeAsync();
            await store.WriteTagAsync(new TagValue("temp", 1, 1, TagQuality.Good, DateTimeOffset.UtcNow.AddDays(-10)));
            await store.WriteTagAsync(new TagValue("temp", 2, 2, TagQuality.Good, DateTimeOffset.UtcNow));

            var deleted = await store.PruneAsync(DateTimeOffset.UtcNow.AddDays(-1));

            Assert.Equal(1, deleted);
            Assert.Equal(1, await store.CountHistoryAsync());
        }
        finally
        {
            Cleanup(path);
        }
    }
}
