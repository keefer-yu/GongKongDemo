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
using CommandAudit = Scada.Core.Control.CommandAudit;
using CommandRequest = Scada.Core.Control.CommandRequest;
using CommandSource = Scada.Core.Control.CommandSource;
using CommandState = Scada.Core.Control.CommandState;
using Scada.Core.Acquisition;
using Scada.Core.Configuration;
using Scada.Core.Demo;
using Scada.Core.Devices;
using Scada.Core.Diagnostics;
using Scada.Core.Tags;
using Scada.Protocol.Modbus;
using Scada.Storage;
using Scada.UI.Wpf.ViewModels;

namespace Scada.Wpf.V4.Control;

/// <summary>
/// V4 WPF 的 ViewModel：在 V3 基础上加联锁、命令、回读、审计。
/// 窗体的按钮只负责“确认 + 调用”，安全逻辑全部在 Scada.Control。
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
    private readonly Scada.Control.InterlockEngine _interlocks;
    private readonly Scada.Control.CommandService _commands;
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
    private string _autoModeText = "当前：手动模式";
    private string _controlStatus = "等待操作";
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
        _history = new SqliteHistoryStore(AppPaths.DataFile("v4-control.db"));
        _interlocks = new Scada.Control.InterlockEngine(_tags, _config.Device.Limits);
        _commands = new Scada.Control.CommandService(_session, _interlocks, _store,
            TimeSpan.FromMilliseconds(300), log: AddLog);

        _alarms.Raised += OnAlarmRaised;
        _alarms.Cleared += OnAlarmChanged;
        _alarms.Acknowledged += OnAlarmChanged;
        _commands.Audited += OnCommandAudited;
        _engine.StatsChanged += OnStatsChanged;

        Tags = new ObservableCollection<TagRowViewModel>(_tags.Select(t => new TagRowViewModel(t)));
        ActiveAlarms = [];
        AlarmHistory = [];
        HistoryRows = [];
        AuditRows = [];
        InterlockRows = [];
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
    public ObservableCollection<AuditRowViewModel> AuditRows { get; }
    public ObservableCollection<InterlockRowViewModel> InterlockRows { get; }
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
    public string AutoModeText { get => _autoModeText; private set => Set(ref _autoModeText, value); }
    public string ControlStatus { get => _controlStatus; private set => Set(ref _controlStatus, value); }

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

    /// <summary>
    /// 二次确认后，把命令交给 CommandService：联锁/写入/回读/审计都在那里。
    /// 这样 WPF、WinForms、REST、MQTT 的行为完全一致。
    /// </summary>
    public async Task ExecuteCommandAsync(string tagId, double value, string label)
    {
        var confirm = MessageBox.Show(
            $"即将执行：{label}\n点位：{tagId}，目标值：{value}\n\n确认执行？",
            "操作确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            ControlStatus = "操作已取消";
            AddLog($"已取消：{label}");
            return;
        }

        ControlStatus = "执行中…";
        var result = await _commands.ExecuteAsync(new CommandRequest
        {
            TagId = tagId,
            Value = value,
            Source = CommandSource.Local,
            Confirmed = true,
        });
        ControlStatus = result.Message;
        if (!result.Success)
            MessageBox.Show(result.Message, "命令未成功", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>急停：按安全顺序逐条执行，每条都走完整命令服务并写审计。</summary>
    public async Task EmergencyStopAsync()
    {
        var confirm = MessageBox.Show(
            "急停将按顺序停止泵、加热器、自动模式并关闭出口阀。\n\n确认执行急停？",
            "急停确认", MessageBoxButton.YesNo, MessageBoxImage.Stop);
        if (confirm != MessageBoxResult.Yes) return;

        foreach (var (tag, value) in new[]
                 {
                     ("pump", 0d), ("heater", 0d), ("autoMode", 0d), ("valve", 0d),
                 })
        {
            var result = await _commands.ExecuteAsync(new CommandRequest
            {
                TagId = tag, Value = value, Source = CommandSource.Local, Confirmed = true,
            });
            AddLog($"急停 {tag}：{result.Message}");
        }
        ControlStatus = "急停序列已执行，请检查审计记录。";
    }

    private void OnCommandAudited(CommandAudit audit)
    {
        // 审计事件在命令线程触发：先落库，再切回 UI 线程改 ObservableCollection。
        _ = _history.WriteAuditAsync(audit);
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => OnCommandAudited(audit));
            return;
        }
        AuditRows.Insert(0, AuditRowViewModel.From(audit));
        while (AuditRows.Count > 300) AuditRows.RemoveAt(AuditRows.Count - 1);
    }

    /// <summary>刷新联锁矩阵；真正执行命令时还会再评估一次，防止工况已变。</summary>
    private void RefreshControl()
    {
        InterlockRows.Clear();
        foreach (var check in _interlocks.EvaluateAll(_store.Snapshot()))
            InterlockRows.Add(InterlockRowViewModel.From(check));

        var auto = _store.Get("autoMode");
        AutoModeText = auto.IsGood && auto.Value != 0 ? "当前：自动模式（设备侧温控）" : "当前：手动模式";
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

        var safeCommand = await _commands.ExecuteAsync(new CommandRequest
        {
            TagId = "pump", Value = 1, Source = CommandSource.Local, Confirmed = true,
        });
        var unsafeCommand = await _commands.ExecuteAsync(new CommandRequest
        {
            TagId = "tempSetpoint", Value = 200, Source = CommandSource.Local, Confirmed = true,
        });

        var dbRows = await _history.CountHistoryAsync();
        await DisconnectAsync();
        if (_simulator is not null) await StopSimulatorAsync();

        var success = stats.Successes >= 2 && hh is not null && dbRows > 0 &&
                      safeCommand.Success && unsafeCommand.State == CommandState.Rejected;
        return (success, new Dictionary<string, object?>
        {
            ["cycles"] = stats.Cycles,
            ["successes"] = stats.Successes,
            ["active_alarms"] = active.Count,
            ["hh_alarm"] = hh?.Message,
            ["history_rows"] = dbRows,
            ["safe_command"] = safeCommand.State.ToString(),
            ["unsafe_command"] = unsafeCommand.State.ToString(),
            ["audit_rows"] = AuditRows.Count,
        });
    }

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

    private void OnAlarmRaised(AlarmRecord record)
    {
        _ = _history.WriteAlarmAsync(record);
        AddLog($"[{record.Level}] {record.Message}");
        RefreshAlarmsSafe();
    }

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
        RefreshControl();
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

