using Scada.Core.Control;
using Scada.Core.Devices;
using Scada.Core.Tags;

namespace Scada.Control;

/// <summary>
/// 命令服务把“写一个点”拆成可审计的状态机：
/// 校验确认 → 联锁 → 写入 → 等待 → 回读 → 比对 → 记录审计。
/// 这一层是 SCADA 与普通 Modbus 调试工具的分水岭。
///
/// 小白重点：
/// - 写成功 ≠ 控制成功；设备可能没接受、被 PLC 逻辑覆盖、或地址写错。
/// - 所以写完后必须“回读”，并把每一步结果写进审计。
/// </summary>
public sealed class CommandService : IAsyncDisposable
{
    private readonly IDeviceSession _session;
    private readonly InterlockEngine _interlocks;
    private readonly TagStore _store;
    private readonly IReadOnlyDictionary<string, TagDefinition> _tags;

    // 同一时刻只允许一个命令执行。否则两个人同时“启泵/停泵”会互相覆盖，
    // 回读结果也会对不上是谁写的。
    private readonly SemaphoreSlim _gate = new(1, 1);

    // 写完等一小段时间再回读，给 PLC 扫描周期留出生效时间。
    private readonly TimeSpan _readbackDelay;
    private readonly TimeSpan _commandTimeout;
    private readonly Action<string>? _log;

    public CommandService(
        IDeviceSession session,
        InterlockEngine interlocks,
        TagStore store,
        TimeSpan? readbackDelay = null,
        TimeSpan? commandTimeout = null,
        Action<string>? log = null)
    {
        _session = session;
        _interlocks = interlocks;
        _store = store;
        _tags = store.Definitions.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        _readbackDelay = readbackDelay ?? TimeSpan.FromMilliseconds(300);
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(5);
        _log = log;
    }

    public event Action<CommandResult>? Completed;
    public event Action<CommandAudit>? Audited;

    /// <summary>
    /// 执行一次命令。无论成功、被拒、失败、超时，都会返回 CommandResult 并触发审计。
    /// </summary>
    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;

        // 第一层：参数校验（这些不需要拿命令锁，也不碰设备）。
        if (!_tags.TryGetValue(request.TagId, out var tag))
            return Finish(request, started, CommandState.Rejected, null,
                $"未知点位：{request.TagId}", Array.Empty<InterlockCheck>());

        if (!tag.Writable)
            return Finish(request, started, CommandState.Rejected, null,
                $"点位 {tag.Name} 是只读点，不能写入。", Array.Empty<InterlockCheck>());

        if (!request.Confirmed)
            return Finish(request, started, CommandState.Rejected, null,
                "关键写操作未确认，已拒绝执行。", Array.Empty<InterlockCheck>());

        if (_session.State != DeviceConnectionState.Online)
            return Finish(request, started, CommandState.Failed, null,
                "设备未连接，命令未执行。", Array.Empty<InterlockCheck>());

        // 第二层：串行化 + 超时，保护“写 → 等 → 回读”这一整段。
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_commandTimeout);

            // 第三层：联锁。用当前快照判断，写设备前最后一道软件防线。
            var checks = _interlocks.Evaluate(request, _store.Snapshot());
            if (checks.Any(c => !c.Passed))
            {
                var reason = string.Join("；", checks.Where(c => !c.Passed).Select(c => c.Reason));
                return Finish(request, started, CommandState.Rejected, null,
                    $"联锁拒绝：{reason}", checks);
            }

            // 第四层：写入。允许重试一次，给临时网络抖动一个机会。
            var writeOk = false;
            Exception? writeError = null;
            for (var attempt = 0; attempt < 2 && !writeOk; attempt++)
            {
                try
                {
                    await _session.WriteAsync(tag, request.Value, timeoutCts.Token).ConfigureAwait(false);
                    writeOk = true;
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    return Finish(request, started, CommandState.Timeout, null,
                        "命令执行超时。", checks);
                }
                catch (Exception ex)
                {
                    writeError = ex;
                    _log?.Invoke($"写入 {tag.Name} 第 {attempt + 1} 次失败：{ex.Message}");
                    if (attempt == 0)
                        await Task.Delay(100, timeoutCts.Token).ConfigureAwait(false);
                }
            }

            if (!writeOk)
                return Finish(request, started, CommandState.Failed, null,
                    $"写入失败：{writeError?.Message}", checks);

            // 第五层：回读 + 比对。这是“写成功”与“控制成功”的分界线。
            try
            {
                // 等 PLC 一个扫描周期，避免刚写进去还没生效就回读。
                await Task.Delay(_readbackDelay, timeoutCts.Token).ConfigureAwait(false);
                var readback = await _session.ReadAsync(new[] { tag }, timeoutCts.Token).ConfigureAwait(false);

                if (!readback.TryGetValue(tag.Id, out var actual) || !actual.IsGood)
                    return Finish(request, started, CommandState.Failed, null,
                        "回读失败：设备没有返回有效值。", checks);

                if (!Matches(tag, request.Value, actual.Value))
                    return Finish(request, started, CommandState.Failed, actual.Value,
                        $"回读不一致：请求 {request.Value:F2}，实际 {actual.Value:F2}。", checks);

                // 把回读值写回 TagStore，界面不用等下一轮采集就能看到结果。
                _store.Update(actual);
                return Finish(request, started, CommandState.Succeeded, actual.Value,
                    $"命令成功，回读 {tag.Format(actual.Value)}。", checks);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return Finish(request, started, CommandState.Timeout, null,
                    "等待回读超时。", checks);
            }
            catch (Exception ex)
            {
                return Finish(request, started, CommandState.Failed, null,
                    $"回读异常：{ex.Message}", checks);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>统一收口：构造结果 + 审计 + 事件。所有返回路径都走这里，保证不漏审计。</summary>
    private CommandResult Finish(
        CommandRequest request,
        DateTimeOffset started,
        CommandState state,
        double? actual,
        string message,
        IReadOnlyList<InterlockCheck> checks)
    {
        var duration = DateTimeOffset.UtcNow - started;
        var success = state == CommandState.Succeeded;
        var result = new CommandResult(
            request.Id, request.TagId, success, state, request.Value, actual,
            message, checks, started, duration);

        var audit = new CommandAudit(
            DateTimeOffset.UtcNow, request.Source, request.TagId, request.Value,
            actual, success, message, duration.TotalMilliseconds);

        _log?.Invoke($"[{state}] {request.TagId} ← {request.Value}: {message}");

        // UI 用它刷新审计表，SQLite/北向可以订阅同一事件做持久化/上报。
        Completed?.Invoke(result);
        Audited?.Invoke(audit);
        return result;
    }

    /// <summary>
    /// 回读比对容差：
    /// - bool 必须完全一致；
    /// - 数值允许 0.5 或 1% 的误差，抵消 PLC 取整/工程值换算误差。
    /// </summary>
    private static bool Matches(TagDefinition tag, double requested, double actual)
    {
        if (tag.DataType == TagDataType.Bool)
            return (requested != 0) == (actual != 0);

        var tolerance = Math.Max(0.5, Math.Abs(requested) * 0.01);
        return Math.Abs(actual - requested) <= tolerance;
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
