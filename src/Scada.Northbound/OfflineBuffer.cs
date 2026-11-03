using System.Text.Json;

namespace Scada.Northbound;

public sealed record BufferedMessage(string Topic, string Payload, DateTimeOffset Timestamp);

/// <summary>
/// 极简“断网补发”：MQTT 发不出去时按 JSONL 落盘，恢复后按顺序重发。
/// 生产环境应换成持久化队列并做幂等，但教学演示足够看到数据不丢。
///
/// 为什么用 JSONL（一行一个 JSON）？
/// - 追加/读取都简单；
/// - 某一行损坏时只跳过这一行，不会导致整个文件无法解析；
/// - 可以直接用记事本看“断网期间到底积压了什么”。
/// </summary>
public sealed class OfflineBuffer
{
    private readonly string _path;
    private readonly int _maxItems;

    // 发布线程/重连线程/UI 统计都会访问，统一用锁保护文件和计数。
    private readonly object _gate = new();
    private int _count;

    public OfflineBuffer(string path, int maxItems = 10000)
    {
        _path = System.IO.Path.GetFullPath(path);
        _maxItems = Math.Max(10, maxItems);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

        // 启动时如果上次还有积压，就把它算进 Count，UI 能显示“待补发”。
        _count = ReadAllUnsafe().Count;
    }

    public string Path => _path;
    public int Count { get { lock (_gate) return _count; } }

    /// <summary>发布失败/未连接时入队。超过上限时丢弃最旧的消息，保留最近数据。</summary>
    public void Enqueue(string topic, string payload)
    {
        lock (_gate)
        {
            var items = ReadAllUnsafe();
            items.Add(new BufferedMessage(topic, payload, DateTimeOffset.UtcNow));

            // 上限保护：演示机磁盘也有限，不能让断网一晚上把盘写满。
            if (items.Count > _maxItems)
                items = items.TakeLast(_maxItems).ToList();

            WriteAllUnsafe(items);
            _count = items.Count;
        }
    }

    /// <summary>读取全部待补发消息，但不清空；补发成功后再调用 Clear。</summary>
    public IReadOnlyList<BufferedMessage> TakeAll()
    {
        lock (_gate)
            return ReadAllUnsafe();
    }

    /// <summary>补发成功后清空文件。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
            _count = 0;
        }
    }

    private List<BufferedMessage> ReadAllUnsafe()
    {
        if (!File.Exists(_path)) return [];
        var result = new List<BufferedMessage>();
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<BufferedMessage>(line);
                if (item is not null) result.Add(item);
            }
            catch (JsonException)
            {
                // 跳过损坏行，避免一条坏数据卡死整个补发
            }
        }
        return result;
    }

    private void WriteAllUnsafe(IReadOnlyList<BufferedMessage> items)
    {
        var lines = items.Select(i => JsonSerializer.Serialize(i));
        File.WriteAllLines(_path, lines);
    }
}
