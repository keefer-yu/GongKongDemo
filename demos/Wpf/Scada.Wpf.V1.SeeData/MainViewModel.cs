using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Scada.Core.Acquisition;
using Scada.Core.Demo;
using Scada.Core.Devices;
using Scada.Core.Diagnostics;
using Scada.Core.Tags;
using Scada.UI.Wpf.ViewModels;

namespace Scada.Wpf.V1.SeeData;

/// <summary>
/// WPF 版 V1 的 ViewModel（MVVM 的 VM）：
/// - 持有采集引擎/标签库；
/// - 暴露可绑定的集合和文字属性；
/// - 用一个 250ms 的 DispatcherTimer 把 TagStore 拉到界面。
/// XAML 只负责长什么样，这里负责数据从哪来。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly CommandLineOptions _options;
    private readonly IReadOnlyList<TagDefinition> _tags;
    private readonly TagStore _store;
    private readonly SimulatedDeviceSession _session;
    private readonly AcquisitionEngine _engine;

    // WPF 的 UI 定时器：Tick 在 UI 线程执行，所以可以安全改绑定属性。
    private readonly DispatcherTimer _timer;

    private bool _isRunning;
    private string _statusText = "已停止";
    private string _cyclesText = "0";
    private string _successText = "—";
    private string _latencyText = "—";
    private string _lastError = "—";

    public MainViewModel(CommandLineOptions options)
    {
        _options = options;
        _tags = DemoPlant.CreateTagDefinitions();
        _store = new TagStore();
        _store.Initialize(_tags);
        _session = new SimulatedDeviceSession(DemoPlant.DeviceId, _tags);
        _engine = new AcquisitionEngine(
            _session, _tags, _store, TimeSpan.FromMilliseconds(500),
            new ReconnectSettings { InitialDelayMs = 500, MaxDelayMs = 4000 },
            AddLog);

        Tags = new ObservableCollection<TagRowViewModel>(_tags.Select(t => new TagRowViewModel(t)));
        Logs = [];
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning,
            ex => AddLog($"启动异常：{ex.Message}"));
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning,
            ex => AddLog($"停止异常：{ex.Message}"));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public ObservableCollection<TagRowViewModel> Tags { get; }
    public ObservableCollection<string> Logs { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(StatusText));
            (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (StopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string CyclesText { get => _cyclesText; private set => Set(ref _cyclesText, value); }
    public string SuccessText { get => _successText; private set => Set(ref _successText, value); }
    public string LatencyText { get => _latencyText; private set => Set(ref _latencyText, value); }
    public string LastError { get => _lastError; private set => Set(ref _lastError, value); }

    public async Task StartIfNeededAsync()
    {
        if (_options.AutoStart || _options.Demo || _options.IsSmoke)
            await StartAsync();
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        // StartAsync 只负责启动后台采集循环，不会阻塞 UI。
        await _engine.StartAsync();
        IsRunning = true;
        StatusText = "运行中";
        AddLog("采集已启动，周期 500 ms。");
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        // 等待后台循环真正退出后再改状态，避免“按钮显示停止但还在读设备”。
        await _engine.StopAsync();
        IsRunning = false;
        StatusText = "已停止";
        AddLog("采集已停止。");
    }

    /// <summary>自动验收：停止采集，检查“扫描次数够多 + 质量 Good”，供 --smoke 使用。</summary>
    public async Task<(bool Success, Dictionary<string, object?> Details)> BuildSmokeResultAsync()
    {
        var stats = _engine.Stats;
        var quality = _store.Get("level").Quality;
        var value = _store.Get("level").Value;
        await StopAsync();

        var success = stats.Cycles >= 2 && stats.Successes >= 2 && quality == TagQuality.Good;
        return (success, new Dictionary<string, object?>
        {
            ["cycles"] = stats.Cycles,
            ["successes"] = stats.Successes,
            ["failures"] = stats.Failures,
            ["level_quality"] = quality.ToString(),
            ["level_value"] = value,
        });
    }

    private void Refresh()
    {
        // 从 TagStore 拉快照，而不是直接读设备；UI 刷新频率和采集周期互不影响。
        foreach (var row in Tags)
            row.Update(_store.Get(row.TagId));

        var stats = _engine.Stats;
        CyclesText = stats.Cycles.ToString();
        SuccessText = $"{stats.SuccessRate:F1}%（成功 {stats.Successes} / 失败 {stats.Failures}）";
        LatencyText = $"{stats.LastLatencyMs:F1} / {stats.AverageLatencyMs:F1} ms";
        LastError = string.IsNullOrEmpty(stats.LastError) ? "—" : stats.LastError;
    }

    private void AddLog(string message)
    {
        // 采集线程会调 AddLog；WPF 的集合只能在 UI 线程改，所以先切回 Dispatcher。
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
    }
}
