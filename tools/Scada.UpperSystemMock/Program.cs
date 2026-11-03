using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Scada.Core.Integration;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var brokerPort = 18830;
var restUrl = "http://127.0.0.1:15080";
var token = "scada-demo-token";
var prefix = "gongkong/line1/reactor";
var auto = false;
var duration = 0;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--port" when i + 1 < args.Length: brokerPort = int.Parse(args[++i]); break;
        case "--rest" when i + 1 < args.Length: restUrl = args[++i]; break;
        case "--token" when i + 1 < args.Length: token = args[++i]; break;
        case "--prefix" when i + 1 < args.Length: prefix = args[++i]; break;
        case "--auto": auto = true; break;
        case "--duration" when i + 1 < args.Length: duration = int.Parse(args[++i]); break;
    }
}

var topics = new TopicNames(prefix);
var factory = new MqttFactory();
var serverOptions = new MqttServerOptionsBuilder()
    .WithDefaultEndpoint()
    .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
    .WithDefaultEndpointPort(brokerPort)
    .Build();
var broker = factory.CreateMqttServer(serverOptions);
await broker.StartAsync();
Console.WriteLine($"内嵌 MQTT Broker: 127.0.0.1:{brokerPort}");

var client = factory.CreateMqttClient();
var telemetryCount = 0;
var ackCount = 0;
var lastCommandId = "";

client.ApplicationMessageReceivedAsync += e =>
{
    var topic = e.ApplicationMessage.Topic;
    var payload = e.ApplicationMessage.ConvertPayloadToString();
    if (topic.EndsWith("/telemetry", StringComparison.Ordinal))
    {
        telemetryCount++;
        try
        {
            var telemetry = JsonSerializer.Deserialize<TelemetryEnvelope>(payload);
            if (telemetry is not null)
            {
                var temp = telemetry.Values.FirstOrDefault(v => v.Id == "temp");
                var level = telemetry.Values.FirstOrDefault(v => v.Id == "level");
                Console.WriteLine($"[遥测 #{telemetry.Sequence}] 液位={level?.Value:F1}% 温度={temp?.Value:F1}°C 质量={temp?.Quality}");
            }
        }
        catch (JsonException) { }
    }
    else if (topic.EndsWith("/alarm", StringComparison.Ordinal))
    {
        Console.WriteLine($"[报警] {payload}");
    }
    else if (topic.EndsWith("/cmd/ack", StringComparison.Ordinal))
    {
        ackCount++;
        Console.WriteLine($"[ACK] {payload}");
    }
    else if (topic.EndsWith("/status", StringComparison.Ordinal))
    {
        Console.WriteLine($"[状态] {payload}");
    }
    return Task.CompletedTask;
};

await client.ConnectAsync(new MqttClientOptionsBuilder()
    .WithTcpServer("127.0.0.1", brokerPort)
    .WithClientId("upper-system-mock")
    .WithCleanSession()
    .Build());
await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
    .WithTopicFilter(topics.All)
    .Build());
Console.WriteLine($"已订阅 {topics.All}");

async Task SendCommandAsync(string tagId, double value)
{
    var command = new RemoteCommand(Guid.NewGuid().ToString("N"), tagId, value, token, "UpperSystemMock");
    lastCommandId = command.Id;
    var message = new MqttApplicationMessageBuilder()
        .WithTopic(topics.Command)
        .WithPayload(JsonSerializer.Serialize(command))
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
        .Build();
    await client.PublishAsync(message);
    Console.WriteLine($"[下发] {tagId} = {value} (id={lastCommandId[..8]})");
}

async Task CallRestAsync()
{
    try
    {
        using var http = new HttpClient { BaseAddress = new Uri(restUrl) };
        http.DefaultRequestHeaders.Add("X-Api-Key", token);
        var tags = await http.GetStringAsync("/api/v1/tags");
        Console.WriteLine($"[REST] GET /api/v1/tags -> {tags[..Math.Min(180, tags.Length)]}...");

        var response = await http.PostAsJsonAsync("/api/v1/tags/tempSetpoint/write", new { value = 70 });
        Console.WriteLine($"[REST] POST tempSetpoint=70 -> {(int)response.StatusCode}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[REST] 调用失败：{ex.Message}");
    }
}

if (auto)
{
    Console.WriteLine("自动演示：3s 后 MQTT 改设定值，6s 后启泵，9s 后调 REST。");
    await Task.Delay(TimeSpan.FromSeconds(3));
    await SendCommandAsync("tempSetpoint", 75);
    await Task.Delay(TimeSpan.FromSeconds(3));
    await SendCommandAsync("pump", 1);
    await Task.Delay(TimeSpan.FromSeconds(3));
    await CallRestAsync();
    if (duration > 0) await Task.Delay(TimeSpan.FromSeconds(duration));
    await Task.Delay(TimeSpan.FromSeconds(2));
    Console.WriteLine($"自动演示结束：遥测 {telemetryCount} 条，ACK {ackCount} 条。");
    if (telemetryCount == 0 || ackCount == 0) Environment.ExitCode = 1;
}
else
{
    Console.WriteLine("命令：set temp 75 | set pump 1 | rest | quit");
    while (true)
    {
        Console.Write("> ");
        var line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) break;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts[0].Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
        if (parts[0].Equals("rest", StringComparison.OrdinalIgnoreCase)) { await CallRestAsync(); continue; }
        if (parts[0].Equals("set", StringComparison.OrdinalIgnoreCase) && parts.Length == 3)
        {
            var tag = parts[1] switch
            {
                "temp" => "tempSetpoint",
                _ => parts[1],
            };
            await SendCommandAsync(tag, double.Parse(parts[2]));
        }
    }
}

await client.DisconnectAsync();
client.Dispose();
broker.Dispose();
Console.WriteLine("上位模拟器已退出。");
