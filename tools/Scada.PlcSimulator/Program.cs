using Scada.Protocol.Modbus;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var port = 15020;
var unitId = (byte)1;
var duration = 0;
var quiet = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i]);
            break;
        case "--unit" when i + 1 < args.Length:
            unitId = byte.Parse(args[++i]);
            break;
        case "--duration" when i + 1 < args.Length:
            duration = int.Parse(args[++i]);
            break;
        case "--quiet":
            quiet = true;
            break;
        case "--help":
            PrintHelp();
            return;
    }
}

await using var host = new PlcSimulatorHost();
host.Frame += frame =>
{
    if (!quiet)
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {(frame.Direction == ModbusDirection.Sent ? "TX" : "RX")}  {frame.Summary}");
};

try
{
    await host.StartAsync(port, unitId);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"启动模拟 PLC 失败：{ex.Message}");
    Environment.ExitCode = 2;
    return;
}

Console.WriteLine($"模拟 PLC 已启动：127.0.0.1:{host.BoundPort}  UnitId={unitId}");
Console.WriteLine("寄存器：HR0=液位×10  HR1-2=温度(float)  HR3=压力×1000  HR4=设定×10");
Console.WriteLine("线圈：  C0=泵  C1=加热器  C2=出口阀  C3=自动模式");
Console.WriteLine("Ctrl+C 停止。");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

if (duration > 0)
    cts.CancelAfter(TimeSpan.FromSeconds(duration));

try
{
    while (!cts.IsCancellationRequested)
    {
        var state = host.Model.Snapshot();
        if (!quiet)
        {
            Console.WriteLine(
                $"液位 {state.Level,6:F1}%   温度 {state.Temperature,6:F1}°C   压力 {state.Pressure:F3}MPa   " +
                $"泵={(state.PumpRunning ? "ON" : "OFF"),-3} 加热={(state.HeaterOn ? "ON" : "OFF"),-3} " +
                $"阀={(state.ValveOpen ? "ON" : "OFF"),-3} 自动={(state.AutoMode ? "ON" : "OFF"),-3} 设定={state.Setpoint:F1}°C");
        }

        await Task.Delay(1000, cts.Token);
    }
}
catch (OperationCanceledException)
{
    // 正常停止
}

await host.StopAsync();
Console.WriteLine("模拟 PLC 已停止。");

static void PrintHelp()
{
    Console.WriteLine("Scada.PlcSimulator [--port 15020] [--unit 1] [--duration 秒] [--quiet]");
}
