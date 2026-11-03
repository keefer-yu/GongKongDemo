using System.Windows;
using System.Windows.Threading;
using Scada.Core.Diagnostics;

namespace Scada.Wpf.V2.Modbus;

public partial class MainWindow : Window
{
    private readonly CommandLineOptions _options;
    private readonly MainViewModel _viewModel;
    private DispatcherTimer? _smokeTimer;

    public MainWindow(CommandLineOptions options)
    {
        InitializeComponent();
        _options = options;
        _viewModel = new MainViewModel(options);
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartIfNeededAsync();
        if (_options.IsSmoke)
            StartSmokeTimer(_options.SmokeSeconds!.Value);
    }

    private void StartSmokeTimer(int seconds)
    {
        _smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, seconds)) };
        _smokeTimer.Tick += async (_, _) =>
        {
            _smokeTimer.Stop();
            var result = await _viewModel.BuildSmokeResultAsync();
            var report = SmokeReport.Write("Scada.Wpf.V2.Modbus", result.Success, result.Details);
            _viewModel.Logs.Insert(0, $"冒烟结果：{(result.Success ? "PASS" : "FAIL")}，报告 {report}");
            Application.Current.Shutdown(result.Success ? 0 : 1);
        };
        _smokeTimer.Start();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _smokeTimer?.Stop();
        await _viewModel.DisposeAsync();
    }
}
