using Scada.Core.Demo;
using Scada.Core.Devices;
using Scada.Core.Tags;

namespace Scada.Tests.Core;

public class SimulatedDeviceSessionTests
{
    [Fact]
    public async Task Read_returns_good_engineering_values()
    {
        var tags = DemoPlant.CreateTagDefinitions();
        await using var session = new SimulatedDeviceSession("sim", tags);
        await session.OpenAsync();

        var values = await session.ReadAsync(tags);

        Assert.Equal(8, values.Count);
        Assert.All(values.Values, v => Assert.Equal(TagQuality.Good, v.Quality));
        Assert.InRange(values["level"].Value, 0, 100);
        Assert.InRange(values["temp"].Value, 0, 150);
    }

    [Fact]
    public async Task Write_setpoint_then_read_back()
    {
        var tags = DemoPlant.CreateTagDefinitions();
        await using var session = new SimulatedDeviceSession("sim", tags);
        await session.OpenAsync();
        var setpoint = tags.Single(t => t.Id == "tempSetpoint");

        await session.WriteAsync(setpoint, 75);
        var values = await session.ReadAsync(tags);

        Assert.Equal(75, values["tempSetpoint"].Value, 1);
    }

    [Fact]
    public async Task Write_pump_coil_then_read_back()
    {
        var tags = DemoPlant.CreateTagDefinitions();
        await using var session = new SimulatedDeviceSession("sim", tags);
        await session.OpenAsync();
        var pump = tags.Single(t => t.Id == "pump");

        await session.WriteAsync(pump, 1);
        var values = await session.ReadAsync(tags);

        Assert.Equal(1, values["pump"].Value);
    }
}
