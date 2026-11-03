using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Scada.Alarms;
using Scada.Core.Acquisition;
using Scada.Core.Configuration;
using Scada.Core.Demo;
using Scada.Core.Devices;
using Scada.Core.Diagnostics;
using Scada.Core.Tags;
using Scada.Protocol.Modbus;
using Scada.Storage;
using Scada.UI.Wpf.ViewModels;

namespace Scada.Wpf.V3.AlarmHistory;

/// <summary>
/// V3 WPF 的 ViewModel：采集 + 报警状态机 + SQLite 历史 + 趋势。
/// 报警/历史逻辑在 Scada.Alarms / Scada.Storage，这里负责把事件翻译成 UI 集合。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly CommandLineOptions _options;
    private readonly DeviceConfig _config;
    private readonly IReadOnlyList<TagDefinition> _tags;
    private readonly TagStore _store;
    private readonly ModbusTcpDeviceSession _session;
    private readonly AcquisitionEngine _engine;
    private readonly AlarmEngine _alarms;
    private readonly SqliteHistoryStore _history;
    private readonly DispatcherTimer _timer;
    private PlcSimulatorHost? _simulator;

    private bool _isSimulatorRunning;
    private bool _isConnecting;
    private bool _statusOnline;
    private bool _statusFault;
    private string _statusText = "未连接";
    private string _cyclesText = "0";
    private string _successText = "—";
    private string _latencyText = "—";
    private string _dbCountText = "0";
    private string _activeCountText = "0";
    private string _alarmBannerText = "✓ 无活动报警";
    private Brush _alarmBannerBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0xD6, 0x8E));
    private string _historySummary = "等待查询…";
    private int _uiTicks;

    public MainViewModel(CommandLineOptions options)
    {
        _options = options;
        var path = options.DeviceConfigPath ?? AppPaths.Resolve(Path.Combine("config", "devices.json"));
        try { _config = ConfigLoader.LoadDevice(path); }
        catch (Exception ex)
        {
            _config = DemoPlant.CreateDeviceConfig();
            ConfigWarning = $"配置读取失败（{ex.Message}），已回退到内置点位。";
        }

        _tags = _config.Tags;
        _store = new TagStore();
        _store.Initialize(_tags);
        _session = new ModbusTcpDeviceSession(_config.Device, _tags);
        _engine = new AcquisitionEngine(
            _session, _tags, _store, TimeSpan.FromMilliseconds(Math.Max(100, _config.Device.ScanIntervalMs)),
            _config.Device.Reconnect, AddLog);
        _alarms = new AlarmEngine(_tags);
        _history = new SqliteHistoryStore(AppPaths.DataFile("v3-history.db"));

        _alarms.Raised += OnAlarmRaised;
        _alarms.Cleared += OnAlarmChanged;
        _alarms.Acknowledged += OnAlarmChanged;
        _engine.StatsChanged += OnStatsChanged;

        Tags = new ObservableCollection<TagRowViewModel>(_tags.Select(t => new TagRowViewModel(t)));
        ActiveAlarms = [];
        AlarmHistory = [];
        HistoryRows = [];
        Logs = [];
        TargetText = $"{_config.Device.Host}:{_config.Device.Port}  Unit={_config.Device.UnitId}";

        StartSimulatorCommand = new AsyncRelayCommand(StartSimulatorAsync, () => !IsSimulatorRunning,
            ex => AddLog($"启动模拟器异常：{ex.Message}"));
        StopSimulatorCommand = new AsyncRelayCommand(StopSimulatorAsync, () => IsSimulatorRunning,
            ex => AddLog($"停止模拟器异常：{ex.Message}"));
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnecting,
            ex => AddLog($"连接异常：{ex.Message}"));
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnecting,
            ex => AddLog($"断开异常：{ex.Message}"));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public ObservableCollection<TagRowViewModel> Tags { get; }
    public ObservableCollection<AlarmRowViewModel> ActiveAlarms { get; }
    public ObservableCollection<AlarmRowViewModel> AlarmHistory { get; }
    public ObservableCollection<HistoryRowViewModel> HistoryRows { get; }
    public ObservableCollection<string> Logs { get; }
    public ICommand StartSimulatorCommand { get; }
    public ICommand StopSimulatorCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public string? ConfigWarning { get; }
    public string TargetText { get; }
    public IReadOnlyList<TagDefinition> TagDefinitions => _tags;

    public event Action<string, double>? TrendPoint;

    public bool IsSimulatorRunning
    {
        get => _isSimulatorRunning;
        private set
        {
            if (!Set(ref _isSimulatorRunning, value)) return;
            (StartSimulatorCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (StopSimulatorCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (!Set(ref _isConnecting, value)) return;
            (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DisconnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool StatusOnline { get => _statusOnline; private set => Set(ref _statusOnline, value); }
    public bool StatusFault { get => _statusFault; private set => Set(ref _statusFault, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string CyclesText { get => _cyclesText; private set => Set(ref _cyclesText, value); }
    public string SuccessText { get => _successText; private set => Set(ref _successText, value); }
    public string LatencyText { get => _latencyText; private set => Set(ref _latencyText, value); }
    public string DbCountText { get => _dbCountText; private set => Set(ref _dbCountText, value); }
    public string ActiveCountText { get => _activeCountText; private set => Set(ref _activeCountText, value); }
    public string AlarmBannerText { get => _alarmBannerText; private set => Set(ref _alarmBannerText, value); }
    public Brush AlarmBannerBrush { get => _alarmBannerBrush; private set => Set(ref _alarmBannerBrush, value); }
    public string HistorySummary { get => _historySummary; private set => Set(ref _historySummary, value); }

    public async Task StartIfNeededAsync()
    {
        if (!string.IsNullOrEmpty(ConfigWarning)) AddLog(ConfigWarning);
        try { await _history.InitializeAsync(); }
        catch (Exception ex) { AddLog($"历史库初始化失败：{ex.Message}"); }

        if (_options.AutoStart || _options.Demo || _options.IsSmoke)
        {
            await StartSimulatorAsync();
            await ConnectAsync();
        }
    }

    public async Task StartSimulatorAsync()
    {
        if (IsSimulatorRunning) return;
        try
        {
            _simulator = new PlcSimulatorHost();
            await _simulator.StartAsync(_config.Device.Port, _config.Device.UnitId);
            IsSimulatorRunning = true;
            AddLog($"模拟 PLC 已启动：127.0.0.1:{_simulator.BoundPort}");
        }
        catch (Exception ex)
        {
            AddLog($"模拟 PLC 启动失败：{ex.Message}");
            MessageBox.Show($"端口 {_config.Device.Port} 启动失败：{ex.Message}", "模拟 PLC",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task StopSimulatorAsync()
    {
        if (_simulator is null) return;
        await _simulator.DisposeAsync();
        _simulator = null;
        IsSimulatorRunning = false;
        AddLog("模拟 PLC 已停止：观察坏质量与重连。");
    }

    public async Task ConnectAsync()
    {
        if (IsConnecting) return;
        await _engine.StartAsync();
        IsConnecting = true;
        AddLog("采集已启动。");
    }

    public async Task DisconnectAsync()
    {
        if (!IsConnecting) return;
        await _engine.StopAsync();
        IsConnecting = false;
        StatusText = "已断开";
        AddLog("采集已停止。");
    }

    public void InjectFault(FaultKind kind, string name)
    {
        if (_simulator is null)
        {
            MessageBox.Show("内置模拟器未启动；真实 PLC 无法注入故障。", "故障注入",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _simulator.Model.InjectFault(kind, TimeSpan.FromSeconds(20));
        AddLog($"已注入故障：{name}，20 秒后自动恢复。");
    }

    public void Acknowledge(string tagId)
    {
        _alarms.Acknowledge(tagId);
        RefreshAlarms();
    }

    public void AcknowledgeAll()
    {
        foreach (var record in _alarms.Active.DistinctBy(a => a.TagId).ToArray())
            _alarms.Acknowledge(record.TagId);
        RefreshAlarms();
    }

    public async Task QueryHistoryAsync(TagDefinition tag, int minutes)
    {
        var to = DateTimeOffset.UtcNow;
        var from = to.AddMinutes(-minutes);
        try
        {
            var rows = await _history.QueryHistoryAsync(tag.Id, from, to, 2000);
            HistoryRows.Clear();
            foreach (var row in rows)
                HistoryRows.Add(HistoryRowViewModel.From(row));
            HistorySummary = $"{tag.Name} · 最近 {minutes} 分钟 · {rows.Count} 行 · {from.ToLocalTime():HH:mm:ss} → {to.ToLocalTime():HH:mm:ss}";
            DbCountText = (await _history.CountHistoryAsync()).ToString();
        }
        catch (Exception ex)
        {
            HistorySummary = $"查询失败：{ex.Message}";
        }
    }

    public string ExportCsv(TagDefinition tag)
    {
        if (HistoryRows.Count == 0)
            throw new InvalidOperationException("请先查询历史数据。");

        var path = AppPaths.DataFile($"history-{tag.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var sb = new StringBuilder();
        sb.AppendLine("time,tag,value,quality");
        foreach (var row in HistoryRows)
            sb.AppendLine($"{row.Time},{row.TagId},{row.Value},{row.Quality}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    public async Task<int> PruneAsync()
    {
        var deleted = await _history.PruneAsync(DateTimeOffset.UtcNow.AddDays(-7));
        DbCountText = (await _history.CountHistoryAsync()).ToString();
        AddLog($"已清理 {deleted} 行 7 天前历史。");
        return deleted;
    }

    public async Task<(bool Success, Dictionary<string, object?> Details)> BuildSmokeResultAsync()
    {
        var stats = _engine.Stats;
        var active = _alarms.Active;
        var hh = active.FirstOrDefault(a => a.Level == AlarmLevel.HH);
        if (hh is not null) _alarms.Acknowledge(hh.TagId);
        var dbRows = await _history.CountHistoryAsync();
        await DisconnectAsync();
        if (_simulator is not null) await StopSimulatorAsync();

        var success = stats.Successes >= 2 && hh is not null && dbRows > 0;
        return (success, new Dictionary<string, object?>
        {
            ["cycles"] = stats.Cycles,
            ["successes"] = stats.Successes,
            ["active_alarms"] = active.Count,
            ["hh_alarm"] = hh?.Message,
            ["history_rows"] = dbRows,
        });
    }

    /// <summary>
    /// 采集成功后：用同一轮快照评估报警并写历史，保证两者数据一致。
    /// 这个方法在后台线程执行，所有 UI 更新都必须经 Dispatcher。
    /// </summary>
    private void OnStatsChanged(AcquisitionStatistics stats)
    {
        if (stats.Successes == 0) return;
        var snapshot = _store.Snapshot();
        _alarms.Evaluate(snapshot);
        if (stats.LastError is null)
            _ = PersistSnapshotAsync(snapshot);
    }

    private async Task PersistSnapshotAsync(IReadOnlyDictionary<string, TagValue> snapshot)
    {
        try { await _history.WriteTagRangeAsync(snapshot.Values).ConfigureAwait(false); }
        catch (Exception ex) { AddLog($"写历史失败：{ex.Message}"); }
    }

    /// <summary>报警出现：落库 + 日志 + 刷新活动/历史列表。</summary>
    private void OnAlarmRaised(AlarmRecord record)
    {
        _ = _history.WriteAlarmAsync(record);
        AddLog($"[{record.Level}] {record.Message}");
        RefreshAlarmsSafe();
    }

    /// <summary>报警确认/恢复：同一条记录状态变化，再写一次形成完整生命周期。</summary>
    private void OnAlarmChanged(AlarmRecord record)
    {
        _ = _history.WriteAlarmAsync(record);
        AddLog($"[{record.State}] {record.TagName} {record.Message}");
        RefreshAlarmsSafe();
    }

    private void RefreshAlarmsSafe()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(RefreshAlarms);
            return;
        }
        RefreshAlarms();
    }

    private void RefreshAlarms()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(RefreshAlarms);
            return;
        }

        ActiveAlarms.Clear();
        foreach (var record in _alarms.Active)
            ActiveAlarms.Add(AlarmRowViewModel.From(record));

        AlarmHistory.Clear();
        foreach (var record in _alarms.Recent.OrderByDescending(r => r.RaisedAt))
            AlarmHistory.Add(AlarmRowViewModel.From(record));

        var active = _alarms.Active;
        ActiveCountText = active.Count.ToString();
        if (active.Count == 0)
        {
            AlarmBannerText = "✓ 无活动报警";
            AlarmBannerBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0xD6, 0x8E));
        }
        else
        {
            var highest = active.OrderByDescending(a => a.Level).First();
            AlarmBannerText = $"⚠ {active.Count} 条活动报警 · 最高 {highest.Level}：{highest.Message}";
            AlarmBannerBrush = new SolidColorBrush(highest.Level is AlarmLevel.HH or AlarmLevel.LL
                ? Color.FromRgb(0xFF, 0x56, 0x60)
                : Color.FromRgb(0xFF, 0xB8, 0x4C));
        }
    }

    private void Refresh()
    {
        // UI 定时器拉 TagStore 快照；趋势点也在这里追加，和采集线程完全解耦。
        foreach (var row in Tags)
        {
            var value = _store.Get(row.TagId);
            row.Update(value);
            if (value.IsGood && row.TagId.Equals("level", StringComparison.OrdinalIgnoreCase))
                TrendPoint?.Invoke("液位", value.Value);
            if (value.IsGood && row.TagId.Equals("temp", StringComparison.OrdinalIgnoreCase))
                TrendPoint?.Invoke("温度", value.Value);
        }

        var stats = _engine.Stats;
        CyclesText = stats.Cycles.ToString();
        SuccessText = $"{stats.SuccessRate:F1}%";
        LatencyText = $"{stats.LastLatencyMs:F1} ms";
        StatusOnline = _session.State == DeviceConnectionState.Online;
        StatusFault = _session.State == DeviceConnectionState.Faulted;
        if (IsConnecting)
            StatusText = StatusOnline ? "已连接 · 采集中" : StatusFault ? "通信故障 · 重连中" : "连接中…";
        else if (IsSimulatorRunning)
            StatusText = "模拟 PLC 已启动";
        else
            StatusText = "未连接";

        RefreshAlarms();
        _uiTicks++;
        if (_uiTicks % 10 == 0) _ = UpdateDbCountAsync();
    }

    private async Task UpdateDbCountAsync()
    {
        try { DbCountText = (await _history.CountHistoryAsync()).ToString(); }
        catch { }
    }

    private void AddLog(string message)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => AddLog(message));
            return;
        }
        Logs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (Logs.Count > 300) Logs.RemoveAt(Logs.Count - 1);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        await _engine.DisposeAsync();
        if (_simulator is not null) await _simulator.DisposeAsync();
        await _history.DisposeAsync();
    }
}
