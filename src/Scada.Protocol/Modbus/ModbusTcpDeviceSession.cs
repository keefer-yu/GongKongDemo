using Scada.Core.Devices;
using Scada.Core.Tags;

namespace Scada.Protocol.Modbus;

/// <summary>
/// 把“点位表”翻译成 Modbus 读写：连续地址按块读取，减少往返次数。
/// V2 之后所有 UI 都通过这个类采样，不再关心 MBAP/PDU。
///
/// 为什么要“按块读”？
/// 8 个点如果逐个读要 8 次网络往返；把连续地址合并成 1 次 FC03/FC01，
/// 延迟和 PLC 负载都会小很多。这是 SCADA 采集优化的第一步。
/// </summary>
public sealed class ModbusTcpDeviceSession : IDeviceSession
{
    private readonly DeviceSettings _settings;
    private readonly IReadOnlyList<TagDefinition> _tags;
    private readonly ModbusTcpClient _client;
    private DeviceConnectionState _state = DeviceConnectionState.Disconnected;

    public ModbusTcpDeviceSession(DeviceSettings settings, IReadOnlyList<TagDefinition> tags)
    {
        _settings = settings;
        _tags = tags;
        _client = new ModbusTcpClient(
            settings.Host,
            settings.Port,
            settings.UnitId,
            TimeSpan.FromMilliseconds(Math.Max(200, settings.TimeoutMs)));
        _client.Frame += frame => Frame?.Invoke(frame);
    }

    public string DeviceId => _settings.Id;
    public DeviceConnectionState State => _state;
    public string? LastError { get; private set; }
    public ModbusTcpClient Client => _client;

    public event Action<DeviceConnectionState>? StateChanged;
    public event Action<ModbusFrame>? Frame;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_state == DeviceConnectionState.Online) return;

        SetState(DeviceConnectionState.Connecting);
        try
        {
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            LastError = null;
            SetState(DeviceConnectionState.Online);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetState(DeviceConnectionState.Faulted);
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<string, TagValue>> ReadAsync(
        IReadOnlyList<TagDefinition> tags, CancellationToken cancellationToken = default)
    {
        if (_state != DeviceConnectionState.Online)
            throw new InvalidOperationException("Modbus 设备未连接。");

        var values = new Dictionary<string, TagValue>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        try
        {
            var addressMap = tags.ToDictionary(t => t.Id, t => TagAddress.Parse(t.Address));

            // 按“区域”分组：线圈、离散输入、保持寄存器、输入寄存器各走不同功能码。
            // 同一区域再按最小/最大地址合并成一次块读取。
            var coilTags = tags.Where(t => t.DataType == TagDataType.Bool &&
                                           addressMap[t.Id].Area == TagArea.Coil).ToArray();
            var discreteTags = tags.Where(t => t.DataType == TagDataType.Bool &&
                                               addressMap[t.Id].Area == TagArea.DiscreteInput).ToArray();
            var holdingTags = tags.Where(t => addressMap[t.Id].Area == TagArea.HoldingRegister).ToArray();
            var inputTags = tags.Where(t => addressMap[t.Id].Area == TagArea.InputRegister).ToArray();

            if (coilTags.Length > 0)
            {
                // 从最小地址读到最大地址，中间没配置的位也会读回来，后面按 offset 切片。
                var start = coilTags.Min(t => addressMap[t.Id].Offset);
                var end = coilTags.Max(t => addressMap[t.Id].Offset);
                var bits = await _client.ReadCoilsAsync((ushort)start, (ushort)(end - start + 1), cancellationToken)
                    .ConfigureAwait(false);
                foreach (var tag in coilTags)
                {
                    var bit = bits[addressMap[tag.Id].Offset - start];
                    values[tag.Id] = new TagValue(tag.Id, bit ? 1 : 0, bit ? 1 : 0, TagQuality.Good, now);
                }
            }

            if (discreteTags.Length > 0)
            {
                var start = discreteTags.Min(t => addressMap[t.Id].Offset);
                var end = discreteTags.Max(t => addressMap[t.Id].Offset);
                var bits = await _client.ReadDiscreteInputsAsync((ushort)start, (ushort)(end - start + 1), cancellationToken)
                    .ConfigureAwait(false);
                foreach (var tag in discreteTags)
                {
                    var bit = bits[addressMap[tag.Id].Offset - start];
                    values[tag.Id] = new TagValue(tag.Id, bit ? 1 : 0, bit ? 1 : 0, TagQuality.Good, now);
                }
            }

            if (holdingTags.Length > 0)
                await ReadRegisterGroupAsync(holdingTags, addressMap, isInput: false, values, now, cancellationToken)
                    .ConfigureAwait(false);

            if (inputTags.Length > 0)
                await ReadRegisterGroupAsync(inputTags, addressMap, isInput: true, values, now, cancellationToken)
                    .ConfigureAwait(false);

            // 任何一个点没被上面的分组覆盖（例如地址区域写错），标 Bad 而不是丢点。
            foreach (var tag in tags)
            {
                if (!values.ContainsKey(tag.Id))
                    values[tag.Id] = TagValue.Bad(tag.Id, $"点位 {tag.Id} 的地址无法读取。", TagQuality.Bad);
            }

            return values;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetState(DeviceConnectionState.Faulted);
            throw;
        }
    }

    private async Task ReadRegisterGroupAsync(
        TagDefinition[] tags,
        IReadOnlyDictionary<string, TagAddress> addressMap,
        bool isInput,
        IDictionary<string, TagValue> values,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // end 用 Offset + RegisterCount - 1：float32 占 2 个寄存器，别少算后半个。
        var start = tags.Min(t => addressMap[t.Id].Offset);
        var end = tags.Max(t => addressMap[t.Id].Offset + t.RegisterCount - 1);
        var quantity = end - start + 1;

        // Modbus 规范一次 FC03 最多 125 个寄存器；超过要拆块。
        // 本项目点位很少，先直接报错提醒；生产项目应在这里做分块循环。
        if (quantity > 125)
            throw new InvalidOperationException("一次读取寄存器超过 125 个，请拆分点位或增加分块逻辑。");

        var registers = isInput
            ? await _client.ReadInputRegistersAsync((ushort)start, (ushort)quantity, cancellationToken).ConfigureAwait(false)
            : await _client.ReadHoldingRegistersAsync((ushort)start, (ushort)quantity, cancellationToken).ConfigureAwait(false);

        foreach (var tag in tags)
        {
            // 块读取的起点是 start，而点位配置的是绝对地址，所以要减去 start 得到切片位置。
            var offset = addressMap[tag.Id].Offset - start;
            var slice = registers.AsSpan(offset, tag.RegisterCount).ToArray();
            var raw = TagValueCodec.DecodeRaw(slice, tag);
            var engineering = TagValueCodec.Decode(slice, tag);
            values[tag.Id] = new TagValue(tag.Id, engineering, raw, TagQuality.Good, now);
        }
    }

    public async Task WriteAsync(TagDefinition tag, double value, CancellationToken cancellationToken = default)
    {
        if (_state != DeviceConnectionState.Online)
            throw new InvalidOperationException("Modbus 设备未连接。");
        if (!tag.Writable)
            throw new InvalidOperationException($"点位 {tag.Id} 是只读点。");

        var address = TagAddress.Parse(tag.Address);
        try
        {
            if (tag.DataType == TagDataType.Bool)
            {
                // bool 点可能接在线圈上（FC05），也可能接在保持寄存器上（FC06 写 0/1）。
                if (address.Area == TagArea.Coil)
                {
                    await _client.WriteSingleCoilAsync((ushort)address.Offset, value != 0, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (address.Area == TagArea.HoldingRegister)
                {
                    await _client.WriteSingleRegisterAsync((ushort)address.Offset, value != 0 ? (ushort)1 : (ushort)0,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                throw new NotSupportedException($"点位 {tag.Id} 的地址不可写。");
            }

            if (address.Area is not (TagArea.HoldingRegister or TagArea.InputRegister))
                throw new NotSupportedException($"点位 {tag.Id} 的地址不可写。");

            var registers = TagValueCodec.Encode(value, tag);
            if (registers.Length == 1)
                await _client.WriteSingleRegisterAsync((ushort)address.Offset, registers[0], cancellationToken)
                    .ConfigureAwait(false);
            else
                await _client.WriteMultipleRegistersAsync((ushort)address.Offset, registers, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetState(DeviceConnectionState.Faulted);
            throw;
        }
    }

    public async Task CloseAsync()
    {
        try
        {
            await _client.DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            SetState(DeviceConnectionState.Disconnected);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
        SetState(DeviceConnectionState.Disconnected);
    }

    private void SetState(DeviceConnectionState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }
}
