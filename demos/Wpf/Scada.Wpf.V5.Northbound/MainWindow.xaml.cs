using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scada.Core.Devices;
using Scada.Core.Diagnostics;
using Scada.UI.Wpf.ViewModels;

namespace Scada.Wpf.V5.Northbound;

public partial class MainWindow : Window
{
    private readonly CommandLineOptions _options;
    private readonly MainViewModel _viewModel;
    private DispatcherTimer? _smokeTimer;
    private DispatcherTimer? _faultTimer;

    public MainWindow(CommandLineOptions options)
    {
        InitializeComponent();
        _options = options;
        _viewModel = new MainViewModel(options);
        DataContext = _viewModel;

        Trend.SetRange(0, 150);
        Trend.AddSeries("液位", new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0xB4)));
        Trend.AddSeries("温度", new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x4C)));
        _viewModel.TrendPoint += (name, value) => Trend.AddPoint(name, value);

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartIfNeededAsync();
        if (HistoryTagCombo.SelectedIndex < 0 && HistoryTagCombo.Items.Count > 1)
            HistoryTagCombo.SelectedIndex = 1;

        if (_options.IsSmoke)
            StartSmokeTimers(_options.SmokeSeconds!.Value);
    }

    private void StartSmokeTimers(int seconds)
    {
        _faultTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _faultTimer.Tick += (_, _) =>
        {
            _faultTimer.Stop();
            _viewModel.InjectFault(FaultKind.HighTemperature, "冒烟超温");
        };
        _faultTimer.Start();

        _smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(12, seconds)) };
        _smokeTimer.Tick += async (_, _) =>
        {
            _smokeTimer.Stop();
            _faultTimer?.Stop();
            var result = await _viewModel.BuildSmokeResultAsync();
            var report = SmokeReport.Write("Scada.Wpf.V5.Northbound", result.Success, result.Details);
            _viewModel.Logs.Insert(0, $"冒烟结果：{(result.Success ? "PASS" : "FAIL")}，报告 {report}");
            Application.Current.Shutdown(result.Success ? 0 : 1);
        };
        _smokeTimer.Start();
    }

    private void AckSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveAlarmGrid.SelectedItem is AlarmRowViewModel row)
            _viewModel.Acknowledge(row.TagId);
    }

    private void AckAll_Click(object sender, RoutedEventArgs e) => _viewModel.AcknowledgeAll();

    private void FaultTemp_Click(object sender, RoutedEventArgs e)
        => _viewModel.InjectFault(FaultKind.HighTemperature, "超温");

    private void FaultPressure_Click(object sender, RoutedEventArgs e)
        => _viewModel.InjectFault(FaultKind.HighPressure, "超压");

    private void FaultLevel_Click(object sender, RoutedEventArgs e)
        => _viewModel.InjectFault(FaultKind.LowLevel, "低液位");

    private async void Command_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string spec }) return;
        var parts = spec.Split(',');
        if (parts.Length != 3) return;
        var tagId = parts[0];
        var value = double.Parse(parts[1], CultureInfo.InvariantCulture);
        await _viewModel.ExecuteCommandAsync(tagId, value, parts[2]);
    }

    private async void Setpoint_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(SetpointBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value))
        {
            MessageBox.Show("请输入合法的设定温度。", "输入错误", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await _viewModel.ExecuteCommandAsync("tempSetpoint", value, "写温度设定");
    }

    private async void EmergencyStop_Click(object sender, RoutedEventArgs e)
        => await _viewModel.EmergencyStopAsync();

    private async void QueryHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryTagCombo.SelectedItem is not TagRowViewModel tag) return;
        var minutes = HistoryRangeCombo.SelectedIndex switch { 0 => 1, 2 => 30, _ => 5 };
        await _viewModel.QueryHistoryAsync(tag.Tag, minutes);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryTagCombo.SelectedItem is not TagRowViewModel tag) return;
        try
        {
            var path = _viewModel.ExportCsv(tag.Tag);
            MessageBox.Show($"已导出到：\n{path}", "导出 CSV", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "导出 CSV", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void Prune_Click(object sender, RoutedEventArgs e)
    {
        var deleted = await _viewModel.PruneAsync();
        MessageBox.Show($"已清理 {deleted} 行 7 天前历史。", "清理", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void StartNorthbound_Click(object sender, RoutedEventArgs e)
        => await _viewModel.StartNorthboundAsync();

    private async void StopNorthbound_Click(object sender, RoutedEventArgs e)
        => await _viewModel.StopNorthboundAsync();

    private void CopyRest_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_viewModel.RestUrl); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void CopyMqtt_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_viewModel.MqttPrefix); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "复制失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _smokeTimer?.Stop();
        _faultTimer?.Stop();
        await _viewModel.DisposeAsync();
    }
}


