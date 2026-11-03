using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Scada.Protocol.Modbus;

/// <summary>
/// 教学版 Modbus TCP 从站：实现 MBAP 与常用功能码，支持多客户端。
/// 它不是生产级协议栈（没有完整并发保护/诊断/网关路由），但足够让 Demo 走真实 TCP。
///
/// 从站就是“设备那一侧”：监听端口 → 收 ADU → 解析功能码 → 读写数据区 → 回 ADU。
/// 真实 PLC 的从站由固件实现，这里用 C# 复刻一遍，方便观察。
/// </summary>
public sealed class ModbusTcpSlave : IAsyncDisposable
{
    private readonly IModbusDataStore _store;

    // 记录所有已连接客户端，Stop 时统一关闭，避免“服务停了但连接还挂着”。
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public ModbusTcpSlave(byte unitId, IModbusDataStore store)
    {
        UnitId = unitId;
        _store = store;
    }

    public byte UnitId { get; }
    public int BoundPort { get; private set; }
    public bool IsRunning => _acceptTask is { IsCompleted: false };

    public event Action<ModbusFrame>? Frame;

    public Task StartAsync(int port = 15020, IPAddress? address = null, CancellationToken cancellationToken = default)
    {
        if (IsRunning) return Task.CompletedTask;

        // port=0 时由系统分配空闲端口，单元测试用这个避免端口冲突。
        _listener = new TcpListener(address ?? IPAddress.Loopback, port);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null) return;

        cts.Cancel();
        _listener?.Stop();

        foreach (var client in _clients.Keys)
        {
            try { client.Close(); } catch { }
        }
        _clients.Clear();

        if (_acceptTask is { } task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        cts.Dispose();
        _cts = null;
        _acceptTask = null;
        _listener = null;
    }

    /// <summary>
    /// 处理一整帧 ADU。纯函数式入口，回环测试直接调用，不必经过网络。
    /// 任何协议错误都转换成 Modbus 异常响应，而不是让连接线程崩掉。
    /// </summary>
    public byte[] ProcessAdu(byte[] adu)
    {
        Frame?.Invoke(new ModbusFrame(DateTimeOffset.UtcNow, ModbusDirection.Received, adu,
            ModbusFrameFormatter.Describe(adu)));

        byte transaction = 0;
        byte unit = 0;
        try
        {
            if (adu.Length < 8)
                throw new ModbusException(0x03, "ADU 长度不足。");

            // MBAP 的事务号是 2 字节大端：adu[0] 是高字节，adu[1] 是低字节。
            // 从站必须原样带回同一个事务号，客户端才能匹配。
            transaction = adu[0];
            var transactionHigh = adu[1];
            var protocol = (adu[2] << 8) | adu[3];
            var length = (adu[4] << 8) | adu[5];
            unit = adu[6];

            if (protocol != 0)
                throw new ModbusException(0x03, "MBAP 协议号必须为 0。");
            if (adu.Length != 6 + length)
                throw new ModbusException(0x03, "MBAP 长度字段与实际长度不一致。");
            if (unit != UnitId)
                throw new ModbusException(0x04, $"从站地址不匹配：期望 {UnitId}，实际 {unit}。");

            var pdu = adu.AsSpan(7).ToArray();
            var responsePdu = ProcessPdu(pdu);
            var response = BuildAdu(transaction, transactionHigh, responsePdu);
            Frame?.Invoke(new ModbusFrame(DateTimeOffset.UtcNow, ModbusDirection.Sent, response,
                ModbusFrameFormatter.Describe(response)));
            return response;
        }
        catch (ModbusException ex)
        {
            // 异常响应：功能码最高位置 1（如 03 → 0x83），再跟一个异常码。
            var response = BuildAdu(transaction, (byte)(adu.Length > 1 ? adu[1] : 0),
                [(byte)((adu.Length > 7 ? adu[7] : 0) | 0x80), ex.ExceptionCode]);
            Frame?.Invoke(new ModbusFrame(DateTimeOffset.UtcNow, ModbusDirection.Sent, response,
                ModbusFrameFormatter.Describe(response)));
            return response;
        }
        catch (Exception)
        {
            // 非协议异常（例如数据区越界），统一回“从站设备故障”0x04，避免连接线程崩掉。
            var response = BuildAdu(transaction, (byte)(adu.Length > 1 ? adu[1] : 0),
                [(byte)((adu.Length > 7 ? adu[7] : 0) | 0x80), 0x04]);
            Frame?.Invoke(new ModbusFrame(DateTimeOffset.UtcNow, ModbusDirection.Sent, response,
                ModbusFrameFormatter.Describe(response)));
            return response;
        }
    }

    private byte[] ProcessPdu(byte[] pdu)
    {
        var function = pdu[0];
        switch (function)
        {
            case ModbusPdu.ReadCoils:
            case ModbusPdu.ReadDiscreteInputs:
            {
                var start = ModbusPdu.ReadU16(pdu, 1);
                var quantity = ModbusPdu.ReadU16(pdu, 3);
                if (quantity is < 1 or > 2000) throw new ModbusException(0x03);
                var bits = function == ModbusPdu.ReadCoils
                    ? _store.ReadCoils(start, quantity)
                    : _store.ReadDiscreteInputs(start, quantity);

                // 线圈按“位”打包：8 个线圈塞进 1 个字节，低位在前。
                // 例如 5 个线圈 → 1 字节，bit0..bit4 分别对应第 1..5 个线圈。
                var byteCount = (quantity + 7) / 8;
                var response = new byte[2 + byteCount];
                response[0] = function;
                response[1] = (byte)byteCount;
                for (var i = 0; i < quantity; i++)
                    if (bits[i])
                        response[2 + i / 8] |= (byte)(1 << (i % 8));
                return response;
            }

            case ModbusPdu.ReadHoldingRegisters:
            case ModbusPdu.ReadInputRegisters:
            {
                var start = ModbusPdu.ReadU16(pdu, 1);
                var quantity = ModbusPdu.ReadU16(pdu, 3);
                if (quantity is < 1 or > 125) throw new ModbusException(0x03);
                var registers = function == ModbusPdu.ReadHoldingRegisters
                    ? _store.ReadHoldingRegisters(start, quantity)
                    : _store.ReadInputRegisters(start, quantity);

                var response = new byte[2 + quantity * 2];
                response[0] = function;
                response[1] = (byte)(quantity * 2);
                for (var i = 0; i < quantity; i++)
                {
                    response[2 + i * 2] = (byte)(registers[i] >> 8);
                    response[3 + i * 2] = (byte)registers[i];
                }
                return response;
            }

            case ModbusPdu.WriteSingleCoil:
            {
                var address = ModbusPdu.ReadU16(pdu, 1);
                // FC05 只有两个合法值：0xFF00=ON，0x0000=OFF。
                // 注意不是 0x0001；这是 Modbus 规范里很容易踩的坑。
                var raw = ModbusPdu.ReadU16(pdu, 3);
                var value = raw switch
                {
                    0xFF00 => true,
                    0x0000 => false,
                    _ => throw new ModbusException(0x03),
                };
                _store.WriteSingleCoil(address, value);
                return [ModbusPdu.WriteSingleCoil, (byte)(address >> 8), (byte)address,
                    (byte)(raw >> 8), (byte)raw];
            }

            case ModbusPdu.WriteSingleRegister:
            {
                var address = ModbusPdu.ReadU16(pdu, 1);
                var value = ModbusPdu.ReadU16(pdu, 3);
                _store.WriteSingleRegister(address, value);
                return [ModbusPdu.WriteSingleRegister, (byte)(address >> 8), (byte)address,
                    (byte)(value >> 8), (byte)value];
            }

            case ModbusPdu.WriteMultipleRegisters:
            {
                var start = ModbusPdu.ReadU16(pdu, 1);
                var quantity = ModbusPdu.ReadU16(pdu, 3);
                var byteCount = pdu[5];

                // 写多寄存器时，ByteCount 必须等于 数量×2；否则视为非法值。
                if (quantity is < 1 or > 123 || byteCount != quantity * 2 || pdu.Length < 6 + byteCount)
                    throw new ModbusException(0x03);

                var values = new ushort[quantity];
                for (var i = 0; i < quantity; i++)
                    values[i] = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);

                _store.WriteMultipleRegisters(start, values);
                return [ModbusPdu.WriteMultipleRegisters, (byte)(start >> 8), (byte)start,
                    (byte)(quantity >> 8), (byte)quantity];
            }

            default:
                throw new ModbusException(0x01);
        }
    }

    /// <summary>把响应 PDU 包上 MBAP 头。事务号原样带回，UnitId 用从站自己的。</summary>
    private byte[] BuildAdu(byte transactionLow, byte transactionHigh, byte[] pdu)
    {
        var adu = new byte[7 + pdu.Length];

        // 参数命名按“低字节在前”的调用习惯，但 MBAP 实际是 big-endian：
        // adu[0] 是高字节，adu[1] 是低字节。调用处已经按这个顺序传。
        adu[0] = transactionLow;
        adu[1] = transactionHigh;
        adu[2] = 0;
        adu[3] = 0;
        var length = 1 + pdu.Length; // 含 UnitId
        adu[4] = (byte)(length >> 8);
        adu[5] = (byte)length;
        adu[6] = UnitId;
        pdu.CopyTo(adu, 7);
        return adu;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // 每个客户端一个独立任务；一个连接慢/断不会阻塞其他连接。
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _clients[client] = 0;
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                var adu = await ReadAduAsync(stream, cancellationToken).ConfigureAwait(false);
                if (adu is null) break;
                var response = ProcessAdu(adu);
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    private static async Task<byte[]?> ReadAduAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // 先精确读 7 字节 MBAP 头，再根据 Length 读剩余部分。
        // 不能假设一次 Read 就能读完整帧：TCP 是字节流，可能拆包/粘包。
        var header = new byte[7];
        try
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        // length 包含 unit id(1) + PDU；unit id 已经在 7 字节头里读掉了。
        var length = (header[4] << 8) | header[5];
        if (length < 2) return null;

        var rest = new byte[length - 1];
        try
        {
            await stream.ReadExactlyAsync(rest, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var adu = new byte[6 + length];
        header.CopyTo(adu, 0);
        rest.CopyTo(adu, 7);
        return adu;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
