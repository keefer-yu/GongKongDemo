using System.Net.Sockets;

namespace Scada.Protocol.Modbus;

/// <summary>
/// 教学版 Modbus TCP 客户端：事务号、MBAP、异常码、超时都在这里。
/// 生产项目建议换成 NModbus / IoTClient，但先看懂这 200 行，协议就不再神秘。
///
/// 一次请求的完整路径：
///   业务参数 → PDU → 加 MBAP 头 → TCP 发出
///   → 读回 7 字节头 → 按 Length 读剩余 → 校验事务号/功能码 → 返回 PDU
/// </summary>
public sealed class ModbusTcpClient : IAsyncDisposable
{
    private readonly IModbusTransport? _injectedTransport;
    private readonly TimeSpan _timeout;

    // Modbus TCP 是“一问一答”协议，没有请求 id 之外的并发复用机制。
    // 同一个连接上同时发两个请求，响应可能串包，所以这里强制串行。
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private IModbusTransport? _transport;

    // 事务号：每个请求 +1，响应必须带回同一个号。用来发现“响应串包/旧响应”。
    private ushort _transactionId;

    public ModbusTcpClient(string host, int port, byte unitId, TimeSpan? timeout = null)
    {
        Host = host;
        Port = port;
        UnitId = unitId;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    public ModbusTcpClient(IModbusTransport transport, byte unitId, TimeSpan? timeout = null)
    {
        _injectedTransport = transport;
        _transport = transport;
        Host = "loopback";
        Port = 0;
        UnitId = unitId;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    public string Host { get; }
    public int Port { get; }
    public byte UnitId { get; }
    public bool IsConnected => _transport is not null;

    public event Action<ModbusFrame>? Frame;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_transport is not null) return;

        var client = new TcpClient { NoDelay = true };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            await client.ConnectAsync(Host, Port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _transport = new TcpModbusTransport(client);
    }

    public async Task DisconnectAsync()
    {
        if (_transport is not null)
            await _transport.DisposeAsync().ConfigureAwait(false);
        _transport = null;
    }

    public async Task<bool[]> ReadCoilsAsync(ushort start, ushort quantity, CancellationToken ct = default)
    {
        var pdu = await SendAsync(ModbusPdu.BuildReadRequest(ModbusPdu.ReadCoils, start, quantity), ct)
            .ConfigureAwait(false);
        return ModbusPdu.ParseBitResponse(pdu, ModbusPdu.ReadCoils, quantity);
    }

    public async Task<bool[]> ReadDiscreteInputsAsync(ushort start, ushort quantity, CancellationToken ct = default)
    {
        var pdu = await SendAsync(ModbusPdu.BuildReadRequest(ModbusPdu.ReadDiscreteInputs, start, quantity), ct)
            .ConfigureAwait(false);
        return ModbusPdu.ParseBitResponse(pdu, ModbusPdu.ReadDiscreteInputs, quantity);
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort start, ushort quantity, CancellationToken ct = default)
    {
        var pdu = await SendAsync(ModbusPdu.BuildReadRequest(ModbusPdu.ReadHoldingRegisters, start, quantity), ct)
            .ConfigureAwait(false);
        return ModbusPdu.ParseRegisterResponse(pdu, ModbusPdu.ReadHoldingRegisters, quantity);
    }

    public async Task<ushort[]> ReadInputRegistersAsync(ushort start, ushort quantity, CancellationToken ct = default)
    {
        var pdu = await SendAsync(ModbusPdu.BuildReadRequest(ModbusPdu.ReadInputRegisters, start, quantity), ct)
            .ConfigureAwait(false);
        return ModbusPdu.ParseRegisterResponse(pdu, ModbusPdu.ReadInputRegisters, quantity);
    }

    public async Task WriteSingleCoilAsync(ushort address, bool value, CancellationToken ct = default)
    {
        var response = await SendAsync(ModbusPdu.BuildWriteSingleCoil(address, value), ct).ConfigureAwait(false);
        ModbusPdu.EnsureFunction(response, ModbusPdu.WriteSingleCoil);
    }

    public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
    {
        var response = await SendAsync(ModbusPdu.BuildWriteSingleRegister(address, value), ct).ConfigureAwait(false);
        ModbusPdu.EnsureFunction(response, ModbusPdu.WriteSingleRegister);
    }

    public async Task WriteMultipleRegistersAsync(ushort start, ushort[] values, CancellationToken ct = default)
    {
        var response = await SendAsync(ModbusPdu.BuildWriteMultipleRegisters(start, values), ct).ConfigureAwait(false);
        ModbusPdu.EnsureFunction(response, ModbusPdu.WriteMultipleRegisters);
    }

    private async Task<byte[]> SendAsync(byte[] requestPdu, CancellationToken cancellationToken)
    {
        var transport = _transport ?? throw new InvalidOperationException("Modbus 客户端尚未连接。");

        // 串行化“写请求 → 读响应”的整个过程，避免两个业务同时读写同一连接。
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 超时是“每次请求”的，不是整个连接生命周期：
            // 设备偶尔慢可以超时重试，但不应该因此永久关闭连接。
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            var transaction = ++_transactionId;
            var request = BuildAdu(transaction, requestPdu);

            // 把 TX/RX 暴露成事件，V2 的报文表就是订阅这里。
            Frame?.Invoke(new ModbusFrame(DateTimeOffset.UtcNow, ModbusDirection.Sent, request,
                ModbusFrameFormatter.Describe(request)));

            byte[] response;
            try
            {
                response = await transport.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 只有“我们自己的超时”才转换成 TimeoutException；
                // 如果是上层取消（例如停止采集），保留 OperationCanceledException。
                throw new TimeoutException($"Modbus 请求超时（{_timeout.TotalMilliseconds:F0} ms）。");
            }

            Frame?.Invoke(new ModbusFrame(DateTimeOffset.UtcNow, ModbusDirection.Received, response,
                ModbusFrameFormatter.Describe(response)));

            ValidateResponse(response, transaction, requestPdu[0]);

            // 返回 PDU（去掉 7 字节 MBAP 头），上层只关心功能码和数据。
            return response[7..];
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>业务 PDU 加上 7 字节 MBAP 头，组成完整 ADU。</summary>
    private byte[] BuildAdu(ushort transaction, byte[] pdu)
    {
        var adu = new byte[7 + pdu.Length];

        // 事务号：高字节在前。
        adu[0] = (byte)(transaction >> 8);
        adu[1] = (byte)transaction;

        // 协议号固定 0（Modbus）。
        adu[2] = 0;
        adu[3] = 0;

        // Length 的值 = UnitId(1) + PDU 长度；它不包含前面的 6 个字节。
        var length = 1 + pdu.Length;
        adu[4] = (byte)(length >> 8);
        adu[5] = (byte)length;

        // UnitId 是 PLC 站号；网关/多站场景下很重要。
        adu[6] = UnitId;
        pdu.CopyTo(adu, 7);
        return adu;
    }

    /// <summary>校验 MBAP/事务号/功能码；异常响应在这里变成 ModbusException。</summary>
    private static void ValidateResponse(byte[] response, ushort expectedTransaction, byte expectedFunction)
    {
        if (response.Length < 8)
            throw new ModbusException(0x03, "响应长度不足。");

        var transaction = (ushort)((response[0] << 8) | response[1]);
        if (transaction != expectedTransaction)
            throw new ModbusException(0x03, "事务号不匹配。");

        var protocol = (response[2] << 8) | response[3];
        if (protocol != 0)
            throw new ModbusException(0x03, "MBAP 协议号不为 0。");

        var length = (response[4] << 8) | response[5];

        // 响应总长度 = 6（MBAP 前 6 字节） + Length（含 UnitId）。
        if (response.Length != 6 + length)
            throw new ModbusException(0x03,
                $"MBAP 长度字段不匹配。响应长度={response.Length}, length={length}, hex={Convert.ToHexString(response)}");

        var function = response[7];

        // 功能码最高位为 1，表示从站返回异常；下一个字节是异常码。
        if ((function & 0x80) != 0)
        {
            if (response.Length < 9)
                throw new ModbusException(0x03, "异常响应缺少异常码。");
            throw new ModbusException(response[8]);
        }

        if (function != expectedFunction)
            throw new ModbusException(0x01, $"功能码不匹配：期望 {expectedFunction}，实际 {function}。");
    }

    public async ValueTask DisposeAsync()
    {
        _requestLock.Dispose();
        if (_injectedTransport is null && _transport is not null)
            await _transport.DisposeAsync().ConfigureAwait(false);
        else if (_injectedTransport is not null)
            await _injectedTransport.DisposeAsync().ConfigureAwait(false);
        _transport = null;
    }
}
