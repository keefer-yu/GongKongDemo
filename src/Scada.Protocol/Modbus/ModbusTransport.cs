using System.Net.Sockets;

namespace Scada.Protocol.Modbus;

/// <summary>传输层抽象：单元测试用回环，运行时用 TCP。</summary>
public interface IModbusTransport : IAsyncDisposable
{
    Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken);
}

public sealed class TcpModbusTransport : IModbusTransport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public TcpModbusTransport(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public async Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        var header = new byte[7];
        await _stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        // length 包含 unit id(1) + PDU；unit id 已经在 7 字节头里读掉了。
        var length = (header[4] << 8) | header[5];
        if (length < 2)
            throw new IOException("Modbus 响应长度非法。");

        var rest = new byte[length - 1];
        await _stream.ReadExactlyAsync(rest, cancellationToken).ConfigureAwait(false);

        var adu = new byte[6 + length];
        header.CopyTo(adu, 0);
        rest.CopyTo(adu, 7);
        return adu;
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>测试用回环：不做网络 I/O，直接调用从站处理 ADU。</summary>
public sealed class LoopbackModbusTransport : IModbusTransport
{
    private readonly Func<byte[], byte[]> _handler;

    public LoopbackModbusTransport(Func<byte[], byte[]> handler) => _handler = handler;

    public Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        => Task.FromResult(_handler(request.ToArray()));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
