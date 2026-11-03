using System.Text.Json;
using System.Text.Json.Serialization;
using Scada.Core.Devices;
using Scada.Core.Tags;

namespace Scada.Core.Configuration;

public sealed class RestSettings
{
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = "http://127.0.0.1:15080";
    public string ApiKey { get; set; } = "scada-demo-token";
}

public sealed class MqttSettings
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 18830;
    public string TopicPrefix { get; set; } = "gongkong/line1/reactor";
    public string ClientId { get; set; } = "scada-reactor-1";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int PublishIntervalMs { get; set; } = 1000;
}

public sealed class OfflineBufferSettings
{
    public string Path { get; set; } = "data/mqtt-outbox.jsonl";
    public int MaxItems { get; set; } = 10000;
}

public sealed class NorthboundConfig
{
    public RestSettings Rest { get; set; } = new();
    public MqttSettings Mqtt { get; set; } = new();
    public OfflineBufferSettings OfflineBuffer { get; set; } = new();
}

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static DeviceConfig LoadDevice(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"找不到设备配置文件：{path}（通常是 config/devices.json）", path);

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<DeviceConfig>(json, Options)
            ?? throw new InvalidDataException($"设备配置为空：{path}");

        if (config.Tags.Count == 0)
            throw new InvalidDataException($"设备配置里没有点位：{path}");

        foreach (var tag in config.Tags)
            _ = TagAddress.Parse(tag.Address); // 启动即校验地址，避免运行到一半才报错

        return config;
    }

    public static NorthboundConfig LoadNorthbound(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到北向配置文件：{path}", path);

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<NorthboundConfig>(json, Options)
            ?? throw new InvalidDataException($"北向配置为空：{path}");
    }
}

public static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string Resolve(string relative)
        => Path.GetFullPath(Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(BaseDirectory, relative));

    public static string EnsureParentDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        return filePath;
    }

    public static string DataFile(string fileName)
        => EnsureParentDirectory(Resolve(Path.Combine("data", fileName)));
}
