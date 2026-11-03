using Microsoft.Data.Sqlite;
using Scada.Alarms;
using Scada.Core.Control;
using Scada.Core.Tags;

namespace Scada.Storage;

public sealed record HistoryPoint(
    DateTimeOffset Timestamp,
    string TagId,
    double Value,
    TagQuality Quality,
    double RawValue);

/// <summary>
/// SQLite 历史库。表结构与真实项目保持同一思路：一行一个采样值，
/// 报警和命令审计单独成表，便于追溯“谁在什么时候改了什么”。
///
/// 三张表各管一件事：
/// - tag_history：点位数值（趋势/报表）
/// - alarm_events：报警生命周期（出现/恢复/确认）
/// - command_audit：谁在什么时候写了什么、回读多少、成功没有
/// </summary>
public sealed class SqliteHistoryStore : IAsyncDisposable
{
    private readonly string _databasePath;

    // SQLite 同一时刻只允许一个写事务；这里用信号量把“写/查”串起来。
    // 演示数据量小，直接串行最简单；生产会换成写队列 + 批量提交。
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteHistoryStore(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            // WAL 模式：读和写可以并行，界面查询历史时不会被采集写入完全堵住。
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS tag_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts_utc TEXT NOT NULL,
                    tag_id TEXT NOT NULL,
                    value REAL NOT NULL,
                    quality TEXT NOT NULL,
                    raw_value REAL NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_tag_history_tag_ts ON tag_history(tag_id, ts_utc);

                CREATE TABLE IF NOT EXISTS alarm_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts_utc TEXT NOT NULL,
                    tag_id TEXT NOT NULL,
                    tag_name TEXT NOT NULL,
                    level TEXT NOT NULL,
                    state TEXT NOT NULL,
                    value REAL NOT NULL,
                    message TEXT NOT NULL,
                    cleared_utc TEXT NULL,
                    acked_utc TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_alarm_ts ON alarm_events(ts_utc);

                CREATE TABLE IF NOT EXISTS command_audit (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts_utc TEXT NOT NULL,
                    source TEXT NOT NULL,
                    tag_id TEXT NOT NULL,
                    requested REAL NOT NULL,
                    actual REAL NULL,
                    ok INTEGER NOT NULL,
                    message TEXT NOT NULL,
                    duration_ms REAL NOT NULL
                );
                """, cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteTagAsync(TagValue value, CancellationToken cancellationToken = default)
        => await WriteTagRangeAsync(new[] { value }, cancellationToken).ConfigureAwait(false);

    public async Task WriteTagRangeAsync(IEnumerable<TagValue> values, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            // 一轮采集的 8 个点放在同一个事务里提交：
            // 要么全写进去，要么全不写，不会出现“只写了一半”的历史。
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO tag_history(ts_utc, tag_id, value, quality, raw_value)
                VALUES ($ts, $tag, $value, $quality, $raw);
                """;

            // 用参数化 SQL，避免拼接字符串，也避免 SQL 注入/转义问题。
            var pTs = command.Parameters.Add("$ts", SqliteType.Text);
            var pTag = command.Parameters.Add("$tag", SqliteType.Text);
            var pValue = command.Parameters.Add("$value", SqliteType.Real);
            var pQuality = command.Parameters.Add("$quality", SqliteType.Text);
            var pRaw = command.Parameters.Add("$raw", SqliteType.Real);

            foreach (var value in values)
            {
                // 统一存 UTC ISO 8601，跨时区/夏令时不会乱；显示时再转本地时间。
                pTs.Value = value.Timestamp.ToUniversalTime().ToString("O");
                pTag.Value = value.TagId;
                pValue.Value = double.IsNaN(value.Value) ? 0 : value.Value;
                pQuality.Value = value.Quality.ToString();
                pRaw.Value = double.IsNaN(value.RawValue) ? 0 : value.RawValue;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAlarmAsync(AlarmRecord alarm, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO alarm_events(ts_utc, tag_id, tag_name, level, state, value, message, cleared_utc, acked_utc)
                VALUES ($ts, $tag, $name, $level, $state, $value, $message, $cleared, $acked);
                """;
            command.Parameters.AddWithValue("$ts", alarm.RaisedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$tag", alarm.TagId);
            command.Parameters.AddWithValue("$name", alarm.TagName);
            command.Parameters.AddWithValue("$level", alarm.Level.ToString());
            command.Parameters.AddWithValue("$state", alarm.State.ToString());
            command.Parameters.AddWithValue("$value", alarm.Value);
            command.Parameters.AddWithValue("$message", alarm.Message);
            command.Parameters.AddWithValue("$cleared", (object?)alarm.ClearedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$acked", (object?)alarm.AcknowledgedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAuditAsync(CommandAudit audit, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO command_audit(ts_utc, source, tag_id, requested, actual, ok, message, duration_ms)
                VALUES ($ts, $source, $tag, $requested, $actual, $ok, $message, $duration);
                """;
            command.Parameters.AddWithValue("$ts", audit.Timestamp.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$source", audit.Source.ToString());
            command.Parameters.AddWithValue("$tag", audit.TagId);
            command.Parameters.AddWithValue("$requested", audit.Requested);
            command.Parameters.AddWithValue("$actual", (object?)audit.Actual ?? DBNull.Value);
            command.Parameters.AddWithValue("$ok", audit.Ok ? 1 : 0);
            command.Parameters.AddWithValue("$message", audit.Message);
            command.Parameters.AddWithValue("$duration", audit.DurationMs);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<HistoryPoint>> QueryHistoryAsync(
        string tagId, DateTimeOffset from, DateTimeOffset to, int limit = 500,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ts_utc, tag_id, value, quality, raw_value
                FROM tag_history
                WHERE tag_id = $tag AND ts_utc >= $from AND ts_utc <= $to
                ORDER BY ts_utc ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$tag", tagId);
            command.Parameters.AddWithValue("$from", from.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$to", to.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));

            // 按时间升序返回，趋势图可以直接从左到右画。
            var result = new List<HistoryPoint>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new HistoryPoint(
                    DateTimeOffset.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetDouble(2),
                    Enum.Parse<TagQuality>(reader.GetString(3)),
                    reader.GetDouble(4)));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AlarmRecord>> QueryAlarmsAsync(
        bool activeOnly = false, int limit = 200, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = activeOnly
                ? """
                  SELECT ts_utc, tag_id, tag_name, level, state, value, message, cleared_utc, acked_utc
                  FROM alarm_events WHERE state IN ('Active','Acked')
                  ORDER BY ts_utc DESC LIMIT $limit;
                  """
                : """
                  SELECT ts_utc, tag_id, tag_name, level, state, value, message, cleared_utc, acked_utc
                  FROM alarm_events ORDER BY ts_utc DESC LIMIT $limit;
                  """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));

            var result = new List<AlarmRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new AlarmRecord
                {
                    TagId = reader.GetString(1),
                    TagName = reader.GetString(2),
                    Level = Enum.Parse<AlarmLevel>(reader.GetString(3)),
                    State = Enum.Parse<AlarmState>(reader.GetString(4)),
                    Value = reader.GetDouble(5),
                    Message = reader.GetString(6),
                    RaisedAt = DateTimeOffset.Parse(reader.GetString(0)),
                    ClearedAt = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
                    AcknowledgedAt = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
                });
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CommandAudit>> QueryAuditAsync(
        int limit = 100, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ts_utc, source, tag_id, requested, actual, ok, message, duration_ms
                FROM command_audit ORDER BY id DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

            var result = new List<CommandAudit>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new CommandAudit(
                    DateTimeOffset.Parse(reader.GetString(0)),
                    Enum.Parse<CommandSource>(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.GetInt64(5) == 1,
                    reader.GetString(6),
                    reader.GetDouble(7)));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> CountHistoryAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM tag_history;";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(result);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM tag_history WHERE ts_utc < $before;";
            command.Parameters.AddWithValue("$before", olderThan.ToUniversalTime().ToString("O"));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Cache=Shared");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();

        // Microsoft.Data.Sqlite 默认会池化连接；不清池，测试/退出时文件仍被占用，
        // Windows 上会报“文件正被另一个进程使用”。
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
