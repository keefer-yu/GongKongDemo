using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Scada.Core.Acquisition;
using Scada.Core.Configuration;
using Scada.Core.Demo;
using Scada.Core.Devices;
using Scada.Core.Diagnostics;
using Scada.Core.Tags;
using Scada.Protocol.Modbus;
using Scada.UI.Wpf.ViewModels;

namespace Scada.Wpf.V2.Modbus;

/// <summary>
/// V2 WPF 的 ViewModel：把 Modbus 采集、模拟 PLC、报文列表接到 XAML。
/// 协议细节在 Scada.Protocol；这里只负责“什么时候连、显示什么”。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly CommandLineOptions _options;
    private readonly DeviceConfig _config;
    private readonly IReadOnlyList<TagDefinition> _tags;
    private readonly TagStore _store;
    private readonly ModbusTcpDeviceSession _session;
    private readonly AcquisitionEngine _engine;
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
    private string _frameCountText = "0";
    private string _lastError = "—";
    private string _targetText = "";

    public MainViewModel(CommandLineOptions options)
    {
        _options = options;
        var path = options.DeviceConfigPath ?? AppPaths.Resolve(Path.Combine("config", "devices.json"));
        try
        {
            _config = ConfigLoader.LoadDevice(path);
        }
        catch (Exception ex)
        {
            _config = DemoPlant.CreateDeviceConfig();
            ConfigWarning = $"配置读取失败（{ex.Message}），已回退到内置点位。";
        }

        _tags = _config.Tags;
        _store = new TagStore();
        _store.Initialize(_tags);
        _session = new ModbusTcpDeviceSession(_config.Device, _tags);
        _session.Frame += OnFrame;
        _engine = new AcquisitionEngine(
            _session, _tags, _store, TimeSpan.FromMilliseconds(Math.Max(100, _config.Device.ScanIntervalMs)),
            _config.Device.Reconnect, AddLog);

        Tags = new ObservableCollection<TagRowViewModel>(_tags.Select(t => new TagRowViewModel(t)));
        Frames = [];
        Logs = [];
        TargetText = $"{_config.Device.Host}:{_config.Device.Port}  Unit={_config.Device.UnitId}  {_config.Device.ScanIntervalMs} ms";

        StartSimulatorCommand = new AsyncRelayCommand(StartSimulatorAsync, () => !IsSimulatorRunning,
            ex => AddLog($"启动模拟器异常：{ex.Message}"));
        StopSimulatorCommand = new AsyncRelayCommand(StopSimulatorAsync, () => IsSimulatorRunning,
            ex => AddLog($"停止模拟器异常：{ex.Message}"));
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnecting,
            ex => AddLog($"连接异常：{ex.Message}"));
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnecting,
            ex => AddLog($"断开异常：{ex.Message}"));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public ObservableCollection<TagRowViewModel> Tags { get; }
    public ObservableCollection<FrameRowViewModel> Frames { get; }
    public ObservableCollection<string> Logs { get; }
    public ICommand StartSimulatorCommand { get; }
    public ICommand StopSimulatorCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public string? ConfigWarning { get; }

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
    public string FrameCountText { get => _frameCountText; private set => Set(ref _frameCountText, value); }
    public string LastError { get => _lastError; private set => Set(ref _lastError, value); }
    public string TargetText { get => _targetText; private set => Set(ref _targetText, value); }

    public async Task StartIfNeededAsync()
    {
        if (!string.IsNullOrEmpty(ConfigWarning)) AddLog(ConfigWarning);
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
        AddLog("模拟 PLC 已停止：观察坏质量与指数退避重连。");
    }

    public async Task ConnectAsync()
    {
        if (IsConnecting) return;
        await _engine.StartAsync();
        IsConnecting = true;
        AddLog("采集已启动，等待 Modbus 连接。");
    }

    public async Task DisconnectAsync()
    {
        if (!IsConnecting) return;
        await _engine.StopAsync();
        IsConnecting = false;
        StatusText = "已断开";
        AddLog("采集与连接已停止。");
    }

    public async Task<(bool Success, Dictionary<string, object?> Details)> BuildSmokeResultAsync()
    {
        var stats = _engine.Stats;
        var quality = _store.Get("level").Quality;
        var frames = Frames.Count;
        await DisconnectAsync();
        if (_simulator is not null) await StopSimulatorAsync();

        var success = stats.Successes >= 2 && quality == TagQuality.Good && frames >= 2;
        return (success, new Dictionary<string, object?>
        {
            ["cycles"] = stats.Cycles,
            ["successes"] = stats.Successes,
            ["failures"] = stats.Failures,
            ["frames"] = frames,
            ["level_quality"] = quality.ToString(),
        });
    }

    private void OnFrame(ModbusFrame frame)
    {
        // 帧事件来自协议线程；ObservableCollection 只能在 UI 线程改，所以先切 Dispatcher。
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => OnFrame(frame));
            return;
        }

        Frames.Insert(0, new FrameRowViewModel
        {
            Time = frame.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
            Direction = frame.Direction == ModbusDirection.Sent ? "TX" : "RX",
            Hex = frame.Hex,
            Summary = frame.Summary,
        });
        while (Frames.Count > 400) Frames.RemoveAt(Frames.Count - 1);
        FrameCountText = Frames.Count.ToString();
    }

    private void Refresh()
    {
        // UI 定时器从 TagStore 拉当前值；采集线程负责写，二者解耦。
        foreach (var row in Tags)
            row.Update(_store.Get(row.TagId));

        var stats = _engine.Stats;
        CyclesText = stats.Cycles.ToString();
        SuccessText = $"{stats.SuccessRate:F1}%";
        LatencyText = $"{stats.LastLatencyMs:F1} ms";
        LastError = string.IsNullOrEmpty(stats.LastError) ? "—" : stats.LastError;

        StatusOnline = _session.State == DeviceConnectionState.Online;
        StatusFault = _session.State == DeviceConnectionState.Faulted;
        if (IsConnecting)
            StatusText = StatusOnline ? "已连接 · 采集中" : StatusFault ? "通信故障 · 重连中" : "连接中…";
        else if (IsSimulatorRunning)
            StatusText = "模拟 PLC 已启动";
        else
            StatusText = "未连接";
    }

    private void AddLog(string message)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => AddLog(message));
            return;
        }

        Logs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (Logs.Count > 200) Logs.RemoveAt(Logs.Count - 1);
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
        if (_simulator is not null)
            await _simulator.DisposeAsync();
    }
}
