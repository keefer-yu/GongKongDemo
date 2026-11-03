using Scada.Core.Devices;

namespace Scada.Tests.Core;

public class ReactorProcessModelTests
{
    [Fact]
    public void Pump_with_open_valve_lowers_level()
    {
        var model = new ReactorProcessModel();
        model.Reset(level: 60, temperature: 25);
        model.SetValve(true);
        model.SetPump(true);

        var before = model.Level;
        model.Tick(1.0);

        Assert.True(model.Level < before);
    }

    [Fact]
    public void Heater_raises_temperature()
    {
        var model = new ReactorProcessModel();
        model.Reset(level: 60, temperature: 25);
        model.SetHeater(true);

        var before = model.Temperature;
        model.Tick(1.0);

        Assert.True(model.Temperature > before);
    }

    [Fact]
    public void Low_level_protection_stops_pump()
    {
        var model = new ReactorProcessModel();
        model.Reset(level: 3, temperature: 25);
        model.SetPump(true);

        model.Tick(0.2);

        Assert.False(model.PumpRunning);
    }

    [Fact]
    public void Over_temperature_protection_stops_heater()
    {
        var model = new ReactorProcessModel();
        model.Reset(level: 60, temperature: 111);
        model.SetHeater(true);

        model.Tick(0.2);

        Assert.False(model.HeaterOn);
    }

    [Fact]
    public void Auto_mode_controls_heater_by_setpoint()
    {
        var model = new ReactorProcessModel();
        model.Reset(level: 60, temperature: 25);
        model.SetSetpoint(70);
        model.SetAuto(true);

        model.Tick(0.2);
        Assert.True(model.HeaterOn);

        model.Reset(level: 60, temperature: 80);
        model.SetSetpoint(70);
        model.SetAuto(true);
        model.Tick(0.2);

        Assert.False(model.HeaterOn);
    }

    [Fact]
    public void Fault_injection_overrides_measurement_and_expires()
    {
        var model = new ReactorProcessModel();
        model.Reset(level: 60, temperature: 25);

        model.InjectFault(FaultKind.HighTemperature, TimeSpan.FromMilliseconds(50));
        model.Tick(0.1);
        Assert.True(model.Temperature >= 95);

        Thread.Sleep(80);
        model.Tick(0.1);
        Assert.True(model.Temperature < 95);
    }

    [Fact]
    public void Holding_register_image_uses_configured_layout()
    {
        var model = new ReactorProcessModel();
        model.Reset(level: 50, temperature: 60, pressure: 0.3);
        model.SetSetpoint(75);

        var registers = model.ReadHoldingRegisters(0, 6);

        Assert.Equal(500, registers[0]);
        Assert.Equal(60f, BitConverter.Int32BitsToSingle((registers[1] << 16) | registers[2]));
        Assert.Equal(300, registers[3]);
        Assert.Equal(750, registers[4]);
    }
}
