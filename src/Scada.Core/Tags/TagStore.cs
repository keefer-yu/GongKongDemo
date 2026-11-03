using System.Collections.Concurrent;

namespace Scada.Core.Tags;

/// <summary>
/// 实时标签库：采集线程写，界面/报警/北向读。
/// 所有值都是不可变 record，读取方拿到的是一致快照。
///
/// 为什么不用普通 Dictionary + 全局锁？
/// - 采集线程每 500ms 写一次，UI 每 250ms 读一次；
/// - ConcurrentDictionary 让“读一个点位”不用抢锁；
/// - Snapshot() 再整体复制一份，UI 遍历时不会遇到“集合被修改”。
/// </summary>
public sealed class TagStore
{
    private readonly ConcurrentDictionary<string, TagValue> _values = new(StringComparer.OrdinalIgnoreCase);

    // 内存趋势环形缓冲：每个点位只保留最近 N 个值，供实时趋势图使用。
    // 这里用 LinkedList 方便从头部删除；真正的历史在 SQLite。
    private readonly ConcurrentDictionary<string, LinkedList<TagValue>> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _historyGate = new();
    private readonly int _historyCapacity;

    public TagStore(int historyCapacity = 600)
    {
        if (historyCapacity < 1) throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        _historyCapacity = historyCapacity;
    }

    public IReadOnlyList<TagDefinition> Definitions { get; private set; } = Array.Empty<TagDefinition>();

    public event Action<TagValue>? TagChanged;

    public void Initialize(IEnumerable<TagDefinition> definitions)
    {
        var list = definitions.ToList();
        Definitions = list;
        foreach (var tag in list)
        {
            // 启动时没有数据，质量必须是 Bad；这比显示 0 更诚实。
            _values[tag.Id] = TagValue.Bad(tag.Id, "未采集");
            lock (_historyGate)
                _history[tag.Id] = new LinkedList<TagValue>();
        }
    }

    public void Update(TagValue value)
    {
        // 先更新实时值，再追加到内存趋势缓冲。
        _values[value.TagId] = value;

        lock (_historyGate)
        {
            var queue = _history.GetOrAdd(value.TagId, _ => new LinkedList<TagValue>());
            queue.AddLast(value);
            while (queue.Count > _historyCapacity)
                queue.RemoveFirst();
        }

        // 事件在锁外触发；订阅者可以按需做 UI 更新/报警/北向发布。
        TagChanged?.Invoke(value);
    }

    public void UpdateRange(IEnumerable<TagValue> values)
    {
        foreach (var value in values)
            Update(value);
    }

    public TagValue Get(string tagId)
        => _values.TryGetValue(tagId, out var value)
            ? value
            : TagValue.Bad(tagId, $"未知点位：{tagId}");

    /// <summary>
    /// 复制一份当前全部点位的快照。UI/报警/北向遍历时用这个，
    /// 而不是直接遍历 _values，避免在遍历过程中被采集线程修改。
    /// </summary>
    public IReadOnlyDictionary<string, TagValue> Snapshot()
        => new Dictionary<string, TagValue>(_values, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TagValue> GetHistory(string tagId)
    {
        lock (_historyGate)
        {
            return _history.TryGetValue(tagId, out var queue)
                ? queue.ToArray()
                : Array.Empty<TagValue>();
        }
    }

    public void ClearHistory()
    {
        lock (_historyGate)
        {
            foreach (var queue in _history.Values)
                queue.Clear();
        }
    }

    /// <summary>
    /// 断线时把全部点位标为 Bad，但保留最后的 Value 便于界面显示“最后值”。
    /// 注意：Value 保留、Quality 变 Bad，趋势/报警会看质量码而不是盲目用最后值。
    /// </summary>
    public void MarkAllBad(string error, TagQuality quality = TagQuality.Bad)
    {
        foreach (var definition in Definitions)
        {
            var last = Get(definition.Id);
            Update(new TagValue(
                definition.Id,
                last.Value,
                last.RawValue,
                quality,
                DateTimeOffset.UtcNow,
                error));
        }
    }
}
