using Scada.Core.Tags;

namespace Scada.Core.Devices;

/// <summary>
/// V1 使用的进程内设备。它不经过网络，但读取路径与真实设备一致：
/// 先把工艺状态编码成寄存器/线圈镜像，再按点位定义解码成工程值。
/// 这样 V1 与 V2 的唯一区别就只剩“数据从哪里来”。
///
/// 三个时间尺度：
/// - 工艺 Tick：200ms，设备自己一直在变化（类似 PLC 扫描）；
/// - 采集周期：500ms，SCADA 来读一次；
/// - UI 刷新：250ms，界面从 TagStore 取快照。
/// 三者解耦，任何一个变慢都不会把另一个卡死。
/// </summary>
public sealed class SimulatedDeviceSession : IDeviceSession
{
    private readonly IReadOnlyList<TagDefinition> _tags;
    private readonly ReactorProcessModel _model;
    private readonly TimeSpan _tickInterval;

    // _disposeCts 管整个对象生命周期；_tickCts 管“打开/关闭”这一轮 Tick 循环。
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _tickCts;
    private Task? _tickTask;

    public SimulatedDeviceSession(
        string deviceId,
        IReadOnlyList<TagDefinition> tags,
        ReactorProcessModel? model = null,
        TimeSpan? tickInterval = null)
    {
        DeviceId = deviceId;
        _tags = tags;
        _model = model ?? new ReactorProcessModel();
        _tickInterval = tickInterval ?? TimeSpan.FromMilliseconds(200);
    }

    public string DeviceId { get; }
    public DeviceConnectionState State { get; private set; } = DeviceConnectionState.Disconnected;
    public string? LastError { get; private set; }

    public ReactorProcessModel Process => _model;

    public event Action<DeviceConnectionState>? StateChanged;

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (State == DeviceConnectionState.Online) return Task.CompletedTask;

        // 打开“设备”时启动工艺 Tick；关闭时取消它。
        _tickCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _tickTask = Task.Run(() => TickLoopAsync(_tickCts.Token), CancellationToken.None);
        SetState(DeviceConnectionState.Online);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, TagValue>> ReadAsync(
        IReadOnlyList<TagDefinition> tags, CancellationToken cancellationToken = default)
    {
        if (State != DeviceConnectionState.Online)
            throw new InvalidOperationException("模拟设备未连接。");

        // 一次性取整张寄存器/线圈镜像，后面按点位地址切片。
        // 真实 Modbus 是“按块读取”，这里模拟同样的数据形状。
        var registers = _model.ReadHoldingRegisters(0, 6);
        var coils = _model.ReadCoils(0, 4);
        var values = new Dictionary<string, TagValue>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        foreach (var tag in tags)
        {
            var address = TagAddress.Parse(tag.Address);
            double raw;
            double engineering;

            if (tag.DataType == TagDataType.Bool)
            {
                // bool 可能来自线圈，也可能来自保持寄存器里的 0/1。
                var bit = address.Area switch
                {
                    TagArea.Coil => coils[address.Offset],
                    TagArea.DiscreteInput => coils[address.Offset],
                    TagArea.HoldingRegister => registers[address.Offset] != 0,
                    TagArea.InputRegister => registers[address.Offset] != 0,
                    _ => false,
                };
                raw = bit ? 1 : 0;
                engineering = raw;
            }
            else
            {
                // float32 要切 2 个寄存器；UInt16 切 1 个。
                var source = address.Area switch
                {
                    TagArea.HoldingRegister => registers.AsSpan(address.Offset, tag.RegisterCount).ToArray(),
                    TagArea.InputRegister => registers.AsSpan(address.Offset, tag.RegisterCount).ToArray(),
                    _ => throw new NotSupportedException($"模拟器不支持把 {tag.Address} 当作数值点位。"),
                };
                raw = TagValueCodec.DecodeRaw(source, tag);
                engineering = TagValueCodec.Decode(source, tag);
            }

            // 质量固定 Good：因为是本地内存，不存在通信失败。
            values[tag.Id] = new TagValue(tag.Id, engineering, raw, TagQuality.Good, now);
        }

        return Task.FromResult<IReadOnlyDictionary<string, TagValue>>(values);
    }

    public Task WriteAsync(TagDefinition tag, double value, CancellationToken cancellationToken = default)
    {
        if (State != DeviceConnectionState.Online)
            throw new InvalidOperationException("模拟设备未连接。");
        if (!tag.Writable)
            throw new InvalidOperationException($"点位 {tag.Id} 是只读点。");

        var address = TagAddress.Parse(tag.Address);
        if (tag.DataType == TagDataType.Bool)
        {
            if (address.Area is not (TagArea.Coil or TagArea.HoldingRegister))
                throw new NotSupportedException($"点位 {tag.Id} 的地址不可写。");
            _model.WriteCoil(address.Offset, value != 0);
            return Task.CompletedTask;
        }

        if (address.Area is not (TagArea.HoldingRegister or TagArea.InputRegister))
            throw new NotSupportedException($"点位 {tag.Id} 的地址不可写。");

        // 工程值 → 寄存器（float 会得到 2 个寄存器），再逐个写进设备模型。
        var registers = TagValueCodec.Encode(value, tag);
        for (var i = 0; i < registers.Length; i++)
            _model.WriteRegister(address.Offset + i, registers[i]);

        return Task.CompletedTask;
    }

    public async Task CloseAsync()
    {
        if (_tickCts is { } cts)
        {
            cts.Cancel();
            if (_tickTask is { } task)
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            cts.Dispose();
            _tickCts = null;
            _tickTask = null;
        }

        SetState(DeviceConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    private async Task TickLoopAsync(CancellationToken cancellationToken)
    {
        // PeriodicTimer 比 Thread.Sleep 更适合异步循环：不会占用一个线程空转。
        using var timer = new PeriodicTimer(_tickInterval);
        var last = DateTimeOffset.UtcNow;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;

                // 用真实经过的时间作为 dt，避免定时器抖动导致工艺速度不一致。
                _model.Tick((now - last).TotalSeconds);
                last = now;
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private void SetState(DeviceConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }
}
