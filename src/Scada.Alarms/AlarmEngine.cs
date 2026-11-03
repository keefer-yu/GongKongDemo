using Scada.Core.Tags;

namespace Scada.Alarms;

/// <summary>
/// 四限报警状态机：Normal → Active → Acked → Cleared。
/// 关键细节：触发用原始阈值，恢复用带回差的阈值；超限必须持续 DelaySeconds 才触发。
///
/// 为什么报警要有“状态”而不是每次超限都发一条消息？
/// - 操作员需要知道“这条报警是否已经被确认”；
/// - 数值在阈值附近抖动时，不能反复刷屏；
/// - 历史库要能还原“什么时候出现、什么时候恢复、谁确认的”。
/// </summary>
public sealed class AlarmEngine
{
    private readonly IReadOnlyList<TagDefinition> _tags;

    // TimeProvider 让测试可以“拨快时钟”，不用真的等 2 秒。
    private readonly TimeProvider _time;
    private readonly Dictionary<string, TagState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AlarmRecord> _recent = [];

    // 采集线程会调用 Evaluate，UI 线程会读 Active/Acknowledge，所以需要锁。
    private readonly object _gate = new();

    public AlarmEngine(IReadOnlyList<TagDefinition> tags, TimeProvider? timeProvider = null)
    {
        _tags = tags;
        _time = timeProvider ?? TimeProvider.System;
        foreach (var tag in tags)
            _states[tag.Id] = new TagState();
    }

    public event Action<AlarmRecord>? Raised;
    public event Action<AlarmRecord>? Cleared;
    public event Action<AlarmRecord>? Acknowledged;

    /// <summary>当前活动报警（Active + Acked）。按严重级别从高到低排序，方便 UI 置顶。</summary>
    public IReadOnlyList<AlarmRecord> Active
    {
        get
        {
            lock (_gate)
                return _states.Values
                    .Select(s => s.Record)
                    .Where(r => r is { IsActive: true })
                    .Cast<AlarmRecord>()
                    .OrderByDescending(r => r.Level)
                    .ThenBy(r => r.RaisedAt)
                    .ToArray();
        }
    }

    public IReadOnlyList<AlarmRecord> Recent
    {
        get
        {
            lock (_gate)
                return _recent.ToArray();
        }
    }

    /// <summary>
    /// 用一轮采集快照评估所有点位。通常在采集成功后调用一次。
    /// 注意：Bad/Stale 值直接跳过——通信断了不能拿“0”去触发/恢复报警。
    /// </summary>
    public void Evaluate(IReadOnlyDictionary<string, TagValue> values)
    {
        var now = _time.GetUtcNow();

        // 先在锁内算出“这次要发哪些事件”，锁外再触发事件，
        // 避免订阅者（UI/SQLite/MQTT）在锁内做耗时操作。
        var raised = new List<AlarmRecord>();
        var cleared = new List<AlarmRecord>();

        lock (_gate)
        {
            foreach (var tag in _tags)
            {
                // 没配置报警或报警关闭的点位跳过。
                if (tag.Alarm is not { Enabled: true } alarm) continue;

                // 质量不好时保持现状：不触发、也不清除，避免 Bad→0 造成误动作。
                if (!values.TryGetValue(tag.Id, out var value) || !value.IsGood) continue;

                var state = _states[tag.Id];

                // 已经活动时用“带回差”的阈值判断是否还在报警；
                // 未活动时用原始阈值判断是否要开始计时。
                var nextLevel = DetermineLevel(value.Value, alarm, useDeadband: state.Record is { IsActive: true });

                if (state.Record is { IsActive: true } active)
                {
                    if (nextLevel is null)
                    {
                        // 恢复：数值离开死区，报警进入 Cleared（历史保留）。
                        active.State = AlarmState.Cleared;
                        active.ClearedAt = now;
                        active.Value = value.Value;
                        cleared.Add(active);
                        state.Record = null;
                        state.PendingLevel = null;
                        state.PendingSince = null;
                    }
                    else if (nextLevel != active.Level)
                    {
                        // 级别升级/降级（例如 H → HH）：重新置为未确认并再发一次事件。
                        active.Level = nextLevel.Value;
                        active.State = AlarmState.Active;
                        active.AcknowledgedAt = null;
                        active.Value = value.Value;
                        active.Message = BuildMessage(tag, nextLevel.Value, value.Value);
                        active.RaisedAt = now;
                        raised.Add(active);
                    }
                    else
                    {
                        // 同级别持续：只更新当前值，不重复发报警。
                        active.Value = value.Value;
                    }

                    continue;
                }

                // 未活动且当前没超限：清掉“待触发”计时。
                if (nextLevel is null)
                {
                    state.PendingLevel = null;
                    state.PendingSince = null;
                    continue;
                }

                // 首次超限或级别变化：重新开始计时。
                if (state.PendingLevel != nextLevel)
                {
                    state.PendingLevel = nextLevel;
                    state.PendingSince = now;
                }

                // 必须连续超限 DelaySeconds 才真正触发；中途恢复会清掉计时。
                var delay = TimeSpan.FromSeconds(Math.Max(0, alarm.DelaySeconds));
                if (now - state.PendingSince >= delay)
                {
                    var record = new AlarmRecord
                    {
                        TagId = tag.Id,
                        TagName = tag.Name,
                        Level = nextLevel.Value,
                        State = AlarmState.Active,
                        Value = value.Value,
                        RaisedAt = now,
                        Message = BuildMessage(tag, nextLevel.Value, value.Value),
                    };
                    state.Record = record;
                    state.PendingLevel = null;
                    state.PendingSince = null;
                    _recent.Add(record);

                    // 只保留最近 200 条，防止演示跑久了内存无限涨。
                    if (_recent.Count > 200) _recent.RemoveAt(0);
                    raised.Add(record);
                }
            }
        }

        foreach (var record in raised) Raised?.Invoke(record);
        foreach (var record in cleared) Cleared?.Invoke(record);
    }

    /// <summary>
    /// 确认报警：只改变“操作员是否已知晓”，不会让报警消失。
    /// 数值恢复后仍然会走 Cleared，历史记录完整保留。
    /// </summary>
    public bool Acknowledge(string tagId)
    {
        var now = _time.GetUtcNow();
        AlarmRecord? record;
        lock (_gate)
        {
            record = _states.TryGetValue(tagId, out var state) ? state.Record : null;
            if (record is null) return false;
            record.State = AlarmState.Acked;
            record.AcknowledgedAt = now;
        }

        Acknowledged?.Invoke(record);
        return true;
    }

    public void Reset()
    {
        lock (_gate)
        {
            foreach (var state in _states.Values)
            {
                state.Record = null;
                state.PendingLevel = null;
                state.PendingSince = null;
            }
            _recent.Clear();
        }
    }

    /// <summary>
    /// 返回当前值命中的最高级别；没有命中返回 null。
    /// useDeadband=true 时把阈值往“安全方向”收一点，这就是恢复回差：
    /// 例如 H=80、Deadband=2：触发要 ≥80，恢复要 &lt;78。
    /// </summary>
    private static AlarmLevel? DetermineLevel(double value, AlarmDefinition alarm, bool useDeadband)
    {
        var deadband = useDeadband ? Math.Abs(alarm.Deadband) : 0;

        // 从最严重的开始判断；HH 优先于 H，LL 优先于 L。
        if (alarm.HH is { } hh && value >= hh - deadband) return AlarmLevel.HH;
        if (alarm.H is { } h && value >= h - deadband) return AlarmLevel.H;
        if (alarm.LL is { } ll && value <= ll + deadband) return AlarmLevel.LL;
        if (alarm.L is { } l && value <= l + deadband) return AlarmLevel.L;
        return null;
    }

    private static string BuildMessage(TagDefinition tag, AlarmLevel level, double value)
        => $"{tag.Name} {(int)level switch { 4 => "高高", 3 => "高", 1 => "低低", 2 => "低", _ => "" }}报警：{tag.Format(value)}";

    private sealed class TagState
    {
        public AlarmRecord? Record { get; set; }
        public AlarmLevel? PendingLevel { get; set; }
        public DateTimeOffset? PendingSince { get; set; }
    }
}
