using Scada.Core.Configuration;
using Scada.Core.Tags;

namespace Scada.Tests.Core;

public class ConfigLoaderTests
{
    [Fact]
    public void Loads_device_and_tag_configuration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scada-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
        {
          "device": {
            "id": "reactor-1",
            "name": "1# 反应釜",
            "protocol": "ModbusTcp",
            "host": "127.0.0.1",
            "port": 15020,
            "unitId": 1,
            "scanIntervalMs": 500,
            "limits": { "pumpMinLevel": 10, "heaterMinLevel": 30, "heaterMaxTemp": 95, "setpointMin": 30, "setpointMax": 95 }
          },
          "tags": [
            {
              "id": "level", "name": "液位", "address": "HR:0", "dataType": "UInt16",
              "scale": 0.1, "unit": "%", "alarm": { "ll": 5, "l": 15, "h": 85, "hh": 95, "deadband": 2, "delaySeconds": 2 }
            }
          ]
        }
        """);

        try
        {
            var config = ConfigLoader.LoadDevice(path);

            Assert.Equal("reactor-1", config.Device.Id);
            Assert.Equal(15020, config.Device.Port);
            Assert.Single(config.Tags);
            Assert.Equal(TagDataType.UInt16, config.Tags[0].DataType);
            Assert.Equal(0.1, config.Tags[0].Scale);
            Assert.Equal(95, config.Tags[0].Alarm!.HH);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_file_throws_a_clear_exception()
    {
        var ex = Assert.Throws<FileNotFoundException>(() =>
            ConfigLoader.LoadDevice(Path.Combine(Path.GetTempPath(), "scada-no-such-file.json")));

        Assert.Contains("devices.json", ex.Message);
    }
}
