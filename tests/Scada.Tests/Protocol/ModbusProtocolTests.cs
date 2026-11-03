using Scada.Core.Demo;
using Scada.Core.Devices;
using Scada.Core.Tags;
using Scada.Protocol.Modbus;

namespace Scada.Tests.Protocol;

public class ModbusProtocolTests
{
    [Fact]
    public async Task Loopback_reads_and_writes_every_supported_area()
    {
        var store = new MemoryModbusDataStore();
        store.Holding[0] = 123;
        store.Holding[1] = 456;
        store.Coils[0] = true;
        var slave = new ModbusTcpSlave(1, store);
        await using var client = new ModbusTcpClient(new LoopbackModbusTransport(slave.ProcessAdu), 1);

        var registers = await client.ReadHoldingRegistersAsync(0, 2);
        var coils = await client.ReadCoilsAsync(0, 2);

        Assert.Equal(new ushort[] { 123, 456 }, registers);
        Assert.True(coils[0]);
        Assert.False(coils[1]);

        await client.WriteSingleCoilAsync(1, true);
        await client.WriteSingleRegisterAsync(3, 77);
        await client.WriteMultipleRegistersAsync(4, new ushort[] { 11, 22 });

        Assert.True(store.Coils[1]);
        Assert.Equal(77, store.Holding[3]);
        Assert.Equal(11, store.Holding[4]);
        Assert.Equal(22, store.Holding[5]);
    }

    [Fact]
    public async Task Illegal_address_returns_modbus_exception()
    {
        var store = new MemoryModbusDataStore();
        var slave = new ModbusTcpSlave(1, store);
        await using var client = new ModbusTcpClient(new LoopbackModbusTransport(slave.ProcessAdu), 1);

        await Assert.ThrowsAsync<ModbusException>(() => client.ReadHoldingRegistersAsync(65535, 2));
    }

    [Fact]
    public async Task Wrong_unit_id_returns_modbus_exception()
    {
        var store = new MemoryModbusDataStore();
        var slave = new ModbusTcpSlave(1, store);
        await using var client = new ModbusTcpClient(new LoopbackModbusTransport(slave.ProcessAdu), 2);

        var ex = await Assert.ThrowsAsync<ModbusException>(() => client.ReadHoldingRegistersAsync(0, 1));
        Assert.Equal(0x04, ex.ExceptionCode);
    }

    [Fact]
    public async Task Tcp_roundtrip_works_on_ephemeral_port()
    {
        var store = new MemoryModbusDataStore();
        store.Holding[0] = 321;
        await using var slave = new ModbusTcpSlave(1, store);
        await slave.StartAsync(port: 0);

        await using var client = new ModbusTcpClient("127.0.0.1", slave.BoundPort, 1);
        await client.ConnectAsync();

        Assert.Equal(321, (await client.ReadHoldingRegistersAsync(0, 1))[0]);
        await client.WriteSingleRegisterAsync(1, 654);
        Assert.Equal(654, store.Holding[1]);
    }

    [Fact]
    public async Task Device_session_maps_tags_and_reconnects()
    {
        await using var host = new PlcSimulatorHost();
        await host.StartAsync(port: 0);
        var config = DemoPlant.CreateDeviceConfig();
        config.Device.Port = host.BoundPort;

        await using var session = new ModbusTcpDeviceSession(config.Device, config.Tags);
        await session.OpenAsync();
        var values = await session.ReadAsync(config.Tags);

        Assert.Equal(TagQuality.Good, values["level"].Quality);
        Assert.Equal(8, values.Count);

        var setpoint = config.Tags.Single(t => t.Id == "tempSetpoint");
        await session.WriteAsync(setpoint, 75);
        var after = await session.ReadAsync(config.Tags);

        Assert.Equal(75, after["tempSetpoint"].Value, 1);
    }

    [Fact]
    public void Frame_formatter_decodes_read_response()
    {
        var adu = new byte[] { 0, 1, 0, 0, 0, 13, 1, 3, 10, 0, 10, 0, 0, 0, 0, 0, 0, 0, 0 };

        var text = ModbusFrameFormatter.Describe(adu);

        Assert.Contains("FC=03", text);
        Assert.Contains("字节数=10", text);
    }
}
