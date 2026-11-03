using System.Diagnostics;
using Scada.Core.Devices;
using Scada.Core.Tags;

namespace Scada.Core.Acquisition;

/// <summary>
/// 采集引擎：一个后台循环，按固定周期读设备、更新标签库、统计成功率。
/// 所有 UI 都只订阅它，不直接碰协议。
///
/// 为什么不放在 UI 定时器里？
/// - 网络读取可能超时/阻塞，放 UI 线程会让界面卡住；
/// - 采集周期和界面刷新周期是两件事，应该解耦；
/// - 断线重连是长任务，必须能独立取消。
/// </summary>
public sealed class AcquisitionEngine : IAsyncDisposable
{
    private readonly IDeviceSession _session;
    private readonly IReadOnlyList<TagDefinition> _tags;
    private readonly TagStore _store;
    private readonly TimeSpan _interval;
    private readonly ReconnectSettings _reconnect;
    private readonly Action<string>? _log;

    // Stats 会被采集线程写、UI 线程读，所以用一个独立小锁保护“统计数字”。
    // 注意：点位值不在这里，点位值由 TagStore 自己的并发结构保护。
    private readonly object _statsGate = new();

    // CTS 用来“取消采集循环”；Task 代表后台循环本身，Stop 时 await 它结束。
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private AcquisitionStatistics _stats = new(0, 0, 0, 0, 0, 0, null, null, false);

    public AcquisitionEngine(
        IDeviceSession session,
        IReadOnlyList<TagDefinition> tags,
        TagStore store,
        TimeSpan interval,
        ReconnectSettings? reconnect = null,
        Action<string>? log = null)
    {
        _session = session;
        _tags = tags;
        _store = store;
        _interval = interval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(500) : interval;
        _reconnect = reconnect ?? new ReconnectSettings();
        _log = log;
    }

    public IDeviceSession Session => _session;
    public bool IsRunning => _runTask is { IsCompleted: false };

    public AcquisitionStatistics Stats
    {
        get { lock (_statsGate) return _stats; }
    }

    public event Action<AcquisitionStatistics>? StatsChanged;
    public event Action<string>? Log;

    /// <summary>启动采集。立即返回，真正的循环在后台任务里跑。</summary>
    public Task StartAsync()
    {
        // 重复点击“启动”不应创建两个循环。
        if (IsRunning) return Task.CompletedTask;

        _runCts = new CancellationTokenSource();
        UpdateStats(s => s with { IsRunning = true, LastError = null });
        LogLine($"采集启动，周期 {_interval.TotalMilliseconds:F0} ms。");

        // Task.Run 把 RunAsync 放到线程池；UI 线程继续响应按钮/重绘。
        _runTask = Task.Run(() => RunAsync(_runCts.Token));
        return Task.CompletedTask;
    }

    /// <summary>停止采集：取消令牌 → 等循环真正退出 → 关闭设备。</summary>
    public async Task StopAsync()
    {
        var cts = _runCts;
        if (cts is null) return;

        // 先取消，让 RunAsync 里的 Task.Delay / ReadAsync 抛出取消异常并退出。
        cts.Cancel();
        try
        {
            if (_runTask is { } task)
                await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止路径：取消导致的异常不需要报错。
        }
        finally
        {
            cts.Dispose();
            _runCts = null;
            _runTask = null;
        }

        // 循环退出后再关连接，避免“还在读就关掉”的竞态。
        try { await _session.CloseAsync().ConfigureAwait(false); }
        catch (Exception ex) { LogLine($"关闭设备失败：{ex.Message}"); }

        UpdateStats(s => s with { IsRunning = false });
        LogLine("采集已停止。");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // 这是采集的“主循环”：连接 → 读 → 写 TagStore → 等下一个周期。
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = _interval;

            try
            {
                // 每次循环都检查连接状态：断线重连后不需要重启整个采集任务。
                if (_session.State != DeviceConnectionState.Online)
                {
                    LogLine($"连接设备 {_session.DeviceId} ...");
                    await _session.OpenAsync(cancellationToken).ConfigureAwait(false);
                    LogLine($"设备 {_session.DeviceId} 已连接。");
                }

                // 只统计“读设备”的耗时，不把 UI 刷新/Task.Delay 算进去。
                var stopwatch = Stopwatch.StartNew();
                var values = await _session.ReadAsync(_tags, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                // 一次更新整批点位，读方拿到的是这一轮的完整快照。
                _store.UpdateRange(values.Values);
                RecordSuccess(stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 用户点了“停止”，不是通信故障，直接退出。
                break;
            }
            catch (Exception ex)
            {
                // 通信失败：先记统计，再把所有点位标 Bad（保留最后值，但界面显示 ERR）。
                RecordFailure(ex.Message);
                _store.MarkAllBad($"通信异常：{ex.Message}");
                LogLine($"采集失败：{ex.Message}");

                // 关闭旧连接，下一轮重新 Open；关闭失败不覆盖真正的读取错误。
                try { await _session.CloseAsync().ConfigureAwait(false); }
                catch { /* 关闭失败不覆盖真正的错误 */ }

                // 指数退避：1s、2s、4s、8s、10s（上限由配置决定）。
                // 避免设备刚重启时被几十个客户端瞬间打爆。
                var failures = Math.Max(1, Stats.ConsecutiveFailures);
                var backoff = _reconnect.InitialDelayMs * Math.Pow(2, Math.Min(8, failures - 1));
                delay = TimeSpan.FromMilliseconds(Math.Min(_reconnect.MaxDelayMs, backoff));
                LogLine($"将在 {delay.TotalMilliseconds:F0} ms 后重连（第 {failures} 次失败）。");
            }

            // 正常周期用 _interval；失败后用上面算出的退避时间。
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void LogLine(string message)
    {
        _log?.Invoke(message);
        Log?.Invoke(message);
    }

    private void RecordSuccess(double latencyMs)
    {
        UpdateStats(s =>
        {
            var successes = s.Successes + 1;

            // 增量平均：新平均 = (旧平均×旧次数 + 本次耗时) / 新次数。
            // 不需要保存所有历史耗时，内存占用恒定。
            var average = ((s.AverageLatencyMs * s.Successes) + latencyMs) / successes;
            return s with
            {
                Cycles = s.Cycles + 1,
                Successes = successes,
                ConsecutiveFailures = 0,
                LastLatencyMs = latencyMs,
                AverageLatencyMs = average,
                LastCycleAt = DateTimeOffset.UtcNow,
                LastError = null,
            };
        });
    }

    private void RecordFailure(string error)
    {
        UpdateStats(s => s with
        {
            Cycles = s.Cycles + 1,
            Failures = s.Failures + 1,
            ConsecutiveFailures = s.ConsecutiveFailures + 1,
            LastCycleAt = DateTimeOffset.UtcNow,
            LastError = error,
        });
    }

    private void UpdateStats(Func<AcquisitionStatistics, AcquisitionStatistics> update)
    {
        AcquisitionStatistics snapshot;

        // 用不可变 record 的 with 更新，锁里只做一次替换；事件在锁外触发，
        // 避免订阅者（UI/北向）在锁内做耗时操作导致采集线程被拖住。
        lock (_statsGate)
        {
            _stats = update(_stats);
            snapshot = _stats;
        }
        StatsChanged?.Invoke(snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _session.DisposeAsync().ConfigureAwait(false);
    }
}
