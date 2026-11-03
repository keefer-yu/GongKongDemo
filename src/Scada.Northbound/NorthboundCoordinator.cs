using System.Text.Json;
using Scada.Alarms;
using Scada.Control;
using Scada.Core.Configuration;
using Scada.Core.Control;
using Scada.Core.Integration;
using Scada.Core.Tags;
using Scada.Storage;

namespace Scada.Northbound;

/// <summary>
/// 把采集/报警/控制接到北向：定时发布遥测、报警事件、状态心跳；
/// 订阅远程命令交给 CommandService，执行完回 ACK。
///
/// 它是 V5 的“接线员”，自己不实现协议细节：
///   TagStore/AlarmEngine → MqttNorthbound + RestApiServer
///   远程命令 → CommandService（联锁/回读/审计）→ ACK
/// 这样 REST 和 MQTT 两条通道共享完全相同的安全逻辑。
/// </summary>
public sealed class NorthboundCoordinator : IAsyncDisposable
{
    private readonly NorthboundConfig _config;
    private readonly TagStore _store;
    private readonly AlarmEngine _alarms;
    private readonly SqliteHistoryStore _history;
    private readonly CommandService _commands;
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly Action<string>? _log;

    private OfflineBuffer? _buffer;
    private MqttNorthbound? _mqtt;
    private RestApiServer? _rest;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private long _sequence;
    private string _lastTelemetry = "";
    private NorthboundStatus _status = new(false, false, false, null, null, 0, 0, DateTimeOffset.UtcNow);

    public NorthboundCoordinator(
        NorthboundConfig config,
        TagStore store,
        AlarmEngine alarms,
        SqliteHistoryStore history,
        CommandService commands,
        string deviceId,
        string deviceName,
        Action<string>? log = null)
    {
        _config = config;
        _store = store;
        _alarms = alarms;
        _history = history;
        _commands = commands;
        _deviceId = deviceId;
        _deviceName = deviceName;
        _log = log;
    }

    public bool IsRunning { get; private set; }
    public string LastTelemetryJson => _lastTelemetry;
    public NorthboundStatus Status => _status;
    public string MqttPrefix => _config.Mqtt.TopicPrefix;
    public string RestUrl => _config.Rest.Url;

    public event Action<string>? Log;
    public event Action<NorthboundStatus>? StatusChanged;
    public event Action<string>? TelemetryPublished;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        IsRunning = true;

        // MQTT 和 REST 可以独立启用；一个失败不阻止另一个继续工作。
        // 例如 Broker 没起来时，REST 仍然可供上位查询。
        _buffer = new OfflineBuffer(AppPaths.Resolve(_config.OfflineBuffer.Path), _config.OfflineBuffer.MaxItems);
        if (_config.Mqtt.Enabled)
        {
            _mqtt = new MqttNorthbound(_config.Mqtt, _buffer, line => Log?.Invoke(line));
            _mqtt.CommandHandler = HandleRemoteCommandAsync;
            await _mqtt.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_config.Rest.Enabled)
        {
            _rest = new RestApiServer(_config.Rest, _store, _alarms, _history, _commands);
            try
            {
                await _rest.StartAsync(cancellationToken).ConfigureAwait(false);
                Log?.Invoke($"REST 已启动：{_config.Rest.Url}");
            }
            catch (Exception ex)
            {
                // 端口冲突等：记录错误，V5 仍然可以靠 MQTT/采集工作。
                Log?.Invoke($"REST 启动失败：{ex.Message}");
            }
        }

        // 报警事件直接转发到 MQTT；遥测由下面的定时循环发布。
        _alarms.Raised += OnAlarmRaised;
        _alarms.Cleared += OnAlarmChanged;
        _alarms.Acknowledged += OnAlarmChanged;

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => PublishLoopAsync(_loopCts.Token));
        Log?.Invoke("北向联动已启动。");
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        // 先停发布循环，再退订报警事件，最后关 MQTT/REST，避免“正在发布时资源被关”。
        _loopCts?.Cancel();
        if (_loopTask is { } task)
        {
            try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;

        _alarms.Raised -= OnAlarmRaised;
        _alarms.Cleared -= OnAlarmChanged;
        _alarms.Acknowledged -= OnAlarmChanged;

        if (_mqtt is not null)
        {
            await _mqtt.StopAsync().ConfigureAwait(false);
            await _mqtt.DisposeAsync().ConfigureAwait(false);
            _mqtt = null;
        }
        if (_rest is not null)
        {
            await _rest.DisposeAsync().ConfigureAwait(false);
            _rest = null;
        }

        UpdateStatus();
        Log?.Invoke("北向联动已停止。");
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        // 默认 1 秒发一包；和 500ms 的采集周期解耦，避免把每个采样点都推给上位。
        var interval = TimeSpan.FromMilliseconds(Math.Max(200, _config.Mqtt.PublishIntervalMs));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await PublishTelemetryAsync(cancellationToken).ConfigureAwait(false);
                UpdateStatus();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PublishTelemetryAsync(CancellationToken ct)
    {
        // 一次快照 → 转成上位友好的 JSON（带 name/unit/quality），再发布。
        // 质量码必须一起发：上位才能区分“0”和“通信坏了”。
        var snapshot = _store.Snapshot();
        var values = snapshot.Values.Select(v =>
        {
            var definition = _store.Definitions.FirstOrDefault(d => d.Id.Equals(v.TagId, StringComparison.OrdinalIgnoreCase));
            return new TelemetryValue(
                v.TagId,
                definition?.Name ?? v.TagId,
                double.IsNaN(v.Value) ? 0 : v.Value,
                definition?.Unit ?? "",
                v.Quality.ToString(),
                double.IsNaN(v.RawValue) ? 0 : v.RawValue);
        }).ToArray();

        // Sequence 单调递增，上位可以用它发现丢包/乱序。
        var envelope = new TelemetryEnvelope(
            DateTimeOffset.UtcNow, _deviceId, _deviceName,
            Interlocked.Increment(ref _sequence), values);

        _lastTelemetry = JsonSerializer.Serialize(envelope);
        TelemetryPublished?.Invoke(_lastTelemetry);

        if (_mqtt is not null)
            await _mqtt.PublishTelemetryAsync(envelope, ct).ConfigureAwait(false);
    }

    private void OnAlarmRaised(AlarmRecord record)
    {
        _ = PublishAlarmAsync(record);
    }

    private void OnAlarmChanged(AlarmRecord record)
    {
        _ = PublishAlarmAsync(record);
    }

    private async Task PublishAlarmAsync(AlarmRecord record)
    {
        if (_mqtt is null) return;
        var envelope = new AlarmEnvelope(
            DateTimeOffset.UtcNow, record.TagId, record.TagName,
            record.Level.ToString(), record.State.ToString(), record.Value, record.Message);
        await _mqtt.PublishAlarmAsync(envelope).ConfigureAwait(false);
    }

    private async Task<CommandAck> HandleRemoteCommandAsync(RemoteCommand command)
    {
        // 远程命令先验 Token；通过后仍然走完整的联锁/回读/审计，不因为“远程”就跳过安全。
        if (!string.Equals(command.Token, _config.Rest.ApiKey, StringComparison.Ordinal))
            return new CommandAck(command.Id, command.TagId, false, "Rejected",
                "Token 无效。", null, DateTimeOffset.UtcNow);

        var result = await _commands.ExecuteAsync(new CommandRequest
        {
            TagId = command.TagId,
            Value = command.Value,
            Source = CommandSource.Mqtt,
            Confirmed = true,
            Token = command.Token,
            Origin = command.Source,
        }).ConfigureAwait(false);

        Log?.Invoke($"远程命令 {command.Id} {command.TagId}={command.Value} → {result.State}");

        // 把命令结果（含回读值/拒绝原因）带回给上位，上位据此显示成功/失败。
        return new CommandAck(
            command.Id, command.TagId, result.Success, result.State.ToString(),
            result.Message, result.ActualValue, DateTimeOffset.UtcNow);
    }

    private void UpdateStatus()
    {
        _status = new NorthboundStatus(
            IsRunning,
            _mqtt?.IsConnected == true,
            _rest?.IsRunning == true,
            _mqtt?.LastError,
            _rest?.LastError,
            _mqtt?.PublishedCount ?? 0,
            _mqtt?.PendingCount ?? 0,
            DateTimeOffset.UtcNow);
        StatusChanged?.Invoke(_status);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}

