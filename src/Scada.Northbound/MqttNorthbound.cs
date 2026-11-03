using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Scada.Core.Configuration;
using Scada.Core.Integration;

namespace Scada.Northbound;

/// <summary>
/// MQTT 北向：遥测/报警/状态发布 + 命令订阅 + 断线缓存补发。
///
/// 主题约定（见 IntegrationContracts.TopicNames）：
///   prefix/telemetry   SCADA → 上位（每秒快照）
///   prefix/alarm       SCADA → 上位（报警事件）
///   prefix/status      SCADA → 上位（online/offline，retained）
///   prefix/cmd         上位 → SCADA（远程命令）
///   prefix/cmd/ack     SCADA → 上位（命令结果）
/// </summary>
public sealed class MqttNorthbound : IAsyncDisposable
{
    private readonly MqttSettings _settings;
    private readonly TopicNames _topics;
    private readonly OfflineBuffer _buffer;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _disposeCts = new();
    private IMqttClient? _client;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private int _published;

    public MqttNorthbound(MqttSettings settings, OfflineBuffer buffer, Action<string>? log = null)
    {
        _settings = settings;
        _topics = new TopicNames(settings.TopicPrefix);
        _buffer = buffer;
        _log = log;
    }

    public bool IsConnected => _client?.IsConnected == true;
    public string? LastError { get; private set; }
    public int PublishedCount => Volatile.Read(ref _published);
    public int PendingCount => _buffer.Count;
    public TopicNames Topics => _topics;

    public Func<RemoteCommand, Task<CommandAck>>? CommandHandler { get; set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled) return;

        _client = new MqttFactory().CreateMqttClient();

        // 收到 cmd 主题时回调；真正的联锁/回读在 Coordinator → CommandService。
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;

        // 断开事件只记录错误；重连由下面的 ConnectionLoop 负责。
        _client.DisconnectedAsync += args =>
        {
            LastError = args.Exception?.Message;
            _log?.Invoke($"MQTT 断开：{args.Exception?.Message}");
            return Task.CompletedTask;
        };

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _loopTask = Task.Run(() => ConnectionLoopAsync(_loopCts.Token));
        await TryConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        _loopCts?.Cancel();
        if (_loopTask is { } task)
        {
            try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        // 主动离线：发 retained 的 offline 状态，让上位知道 SCADA 下线了。
        if (_client is { IsConnected: true })
        {
            try
            {
                await PublishAsync(_topics.Status, "offline", retain: true, CancellationToken.None).ConfigureAwait(false);
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
            catch { }
        }

        _client?.Dispose();
        _client = null;
        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;
    }

    public Task PublishTelemetryAsync(TelemetryEnvelope envelope, CancellationToken ct = default)
        => PublishJsonAsync(_topics.Telemetry, envelope, false, ct);

    public Task PublishAlarmAsync(AlarmEnvelope envelope, CancellationToken ct = default)
        => PublishJsonAsync(_topics.Alarm, envelope, false, ct);

    public async Task PublishJsonAsync<T>(string topic, T payload, bool retain, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await PublishAsync(topic, json, retain, ct).ConfigureAwait(false);
    }

    public async Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        if (!_settings.Enabled) return;

        // 未连接：直接进离线缓存，等重连后补发。
        if (_client is null || !_client.IsConnected)
        {
            _buffer.Enqueue(topic, payload);
            return;
        }

        try
        {
            // 遥测用 QoS0：高频数据，允许偶尔丢一条，换取低开销。
            // 命令/ACK 对可靠性要求更高，生产可改 QoS1 并做去重。
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .WithRetainFlag(retain)
                .Build();
            await _client.PublishAsync(message, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _published);
        }
        catch (Exception ex)
        {
            // 发布失败也入队，避免“连接看似在、实际发不出去”时丢数据。
            LastError = ex.Message;
            _buffer.Enqueue(topic, payload);
            _log?.Invoke($"MQTT 发布失败，已缓存：{ex.Message}");
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
    {
        // 每 5 秒检查一次：没连上就重连；连上且有待补发就补发。
        // 生产环境建议用指数退避 + 随机抖动，避免大量 SCADA 同时重连打爆 Broker。
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_client is { IsConnected: false })
                    await TryConnectAsync(cancellationToken).ConfigureAwait(false);

                if (_client is { IsConnected: true } && _buffer.Count > 0)
                    await FlushBufferAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TryConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is null || _client.IsConnected) return;

        try
        {
            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(_settings.Host, _settings.Port)
                .WithClientId(_settings.ClientId)
                .WithCleanSession()
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(20))
                // LWT（遗言）：如果 SCADA 进程崩溃/网络断开，Broker 会自动替它发 offline。
                .WithWillTopic(_topics.Status)
                .WithWillPayload("offline");

            if (!string.IsNullOrWhiteSpace(_settings.Username))
                builder.WithCredentials(_settings.Username, _settings.Password);

            await _client.ConnectAsync(builder.Build(), cancellationToken).ConfigureAwait(false);

            // 只订阅命令主题；遥测/报警是 SCADA 发布，不需要订阅。
            await _client.SubscribeAsync(
                new MqttClientSubscribeOptionsBuilder().WithTopicFilter(_topics.Command).Build(),
                cancellationToken).ConfigureAwait(false);

            LastError = null;
            await PublishAsync(_topics.Status, "online", retain: true, cancellationToken).ConfigureAwait(false);
            _log?.Invoke($"MQTT 已连接 {_settings.Host}:{_settings.Port}，主题 {_topics.Prefix}/#");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log?.Invoke($"MQTT 连接失败：{ex.Message}");
        }
    }

    private async Task FlushBufferAsync(CancellationToken cancellationToken)
    {
        // 先读全部、逐条发；只有全部成功后才清空文件。
        // 中途失败会保留剩余缓存（可能重复发已成功的那几条，生产要靠幂等键去重）。
        var pending = _buffer.TakeAll();
        if (pending.Count == 0) return;

        foreach (var item in pending)
        {
            try
            {
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(item.Topic)
                    .WithPayload(item.Payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                    .Build();
                await _client!.PublishAsync(message, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _published);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log?.Invoke($"补发失败，保留缓存：{ex.Message}");
                return;
            }
        }

        _buffer.Clear();
        _log?.Invoke($"离线缓存已补发 {pending.Count} 条。");
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var payload = e.ApplicationMessage.ConvertPayloadToString();
            var command = JsonSerializer.Deserialize<RemoteCommand>(payload);
            if (command is null) return;

            // 真正的联锁/写入/回读由 Coordinator 注入的 CommandHandler 完成。
            // 这里只负责“收到命令 → 调处理器 → 把 ACK 发回去”。
            var ack = CommandHandler is null
                ? new CommandAck(command.Id, command.TagId, false, "Rejected", "SCADA 未配置命令处理器。", null,
                    DateTimeOffset.UtcNow)
                : await CommandHandler(command).ConfigureAwait(false);

            // ACK 带上 command.Id，上位才能知道是哪一条命令的结果。
            await PublishJsonAsync(_topics.CommandAck, ack, false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"处理 MQTT 命令失败：{ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }
}
