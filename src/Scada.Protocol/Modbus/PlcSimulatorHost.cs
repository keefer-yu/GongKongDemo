using System.Net;
using Scada.Core.Devices;

namespace Scada.Protocol.Modbus;

/// <summary>
/// 把“反应釜工艺模型 + Modbus 从站”打包成一个可启动/停止的模拟 PLC。
/// V2 界面可以一键启动它；也可以由 Scada.PlcSimulator 控制台独立托管。
/// </summary>
public sealed class PlcSimulatorHost : IAsyncDisposable
{
    private readonly ReactorProcessModel _model = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private ModbusTcpSlave? _slave;
    private CancellationTokenSource? _tickCts;
    private Task? _tickTask;

    public ReactorProcessModel Model => _model;
    public ModbusTcpSlave? Slave => _slave;
    public int BoundPort => _slave?.BoundPort ?? 0;
    public bool IsRunning => _slave?.IsRunning == true;

    public event Action<ModbusFrame>? Frame;

    public async Task StartAsync(int port = 15020, byte unitId = 1, IPAddress? address = null,
        CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;

        _model.Reset();
        _slave = new ModbusTcpSlave(unitId, new ReactorModbusDataStore(_model));
        _slave.Frame += frame => Frame?.Invoke(frame);
        await _slave.StartAsync(port, address ?? IPAddress.Loopback, cancellationToken).ConfigureAwait(false);

        _tickCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _tickTask = Task.Run(() => TickLoopAsync(_tickCts.Token));
    }

    public async Task StopAsync()
    {
        if (_tickCts is { } tickCts)
        {
            tickCts.Cancel();
            if (_tickTask is { } task)
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            tickCts.Dispose();
            _tickCts = null;
            _tickTask = null;
        }

        if (_slave is { } slave)
        {
            await slave.StopAsync().ConfigureAwait(false);
            _slave = null;
        }
    }

    private async Task TickLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        var last = DateTimeOffset.UtcNow;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                _model.Tick((now - last).TotalSeconds);
                last = now;
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }
}

/// <summary>把 Modbus 数据区请求转发给工艺模型。</summary>
public sealed class ReactorModbusDataStore : IModbusDataStore
{
    private readonly ReactorProcessModel _model;

    public ReactorModbusDataStore(ReactorProcessModel model) => _model = model;

    public ushort[] ReadHoldingRegisters(int start, int count) => _model.ReadHoldingRegisters(start, count);
    public ushort[] ReadInputRegisters(int start, int count) => _model.ReadInputRegisters(start, count);
    public bool[] ReadCoils(int start, int count) => _model.ReadCoils(start, count);
    public bool[] ReadDiscreteInputs(int start, int count) => _model.ReadDiscreteInputs(start, count);
    public void WriteSingleCoil(int address, bool value) => _model.WriteCoil(address, value);
    public void WriteSingleRegister(int address, ushort value) => _model.WriteRegister(address, value);

    public void WriteMultipleRegisters(int start, ReadOnlySpan<ushort> values)
    {
        for (var i = 0; i < values.Length; i++)
            _model.WriteRegister(start + i, values[i]);
    }
}
