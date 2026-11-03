using System.ComponentModel;
using Scada.Core.Acquisition;
using Scada.Core.Configuration;
using Scada.Core.Demo;
using Scada.Core.Devices;
using Scada.Core.Diagnostics;
using Scada.Core.Tags;
using Scada.Protocol.Modbus;
using Scada.UI.WinForms.Controls;
using Scada.UI.WinForms.Theme;
using Scada.UI.WinForms.ViewModels;

namespace Scada.WinForms.V2.Modbus;

/// <summary>
/// V2：把 V1 的“内存模拟”换成真正的 Modbus TCP。
/// 新增：连接参数、寄存器映射、原始报文、断线重连。
/// </summary>
public sealed class MainForm : Form
{
    private readonly CommandLineOptions _options;
    private readonly DeviceConfig _config;
    private readonly IReadOnlyList<TagDefinition> _tags;
    private readonly TagStore _store;
    private readonly ModbusTcpDeviceSession _session;
    private readonly AcquisitionEngine _engine;
    private readonly Dictionary<string, TagCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly BindingList<TagRow> _rows = [];
    private readonly Dictionary<string, TagRow> _rowMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly BindingList<FrameRow> _frames = [];
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 250 };
    private System.Windows.Forms.Timer? _smokeTimer;
    private PlcSimulatorHost? _simulator;

    private readonly StatusLamp _statusLamp = new();
    private readonly Label _statusLabel = new();
    private readonly Label _cyclesLabel = new();
    private readonly Label _successLabel = new();
    private readonly Label _latencyLabel = new();
    private readonly Label _frameCountLabel = new();
    private readonly Label _lastErrorLabel = new();
    private readonly Label _targetLabel = new();
    private readonly ListBox _logList = new();
    private readonly DataGridView _tagGrid = new();
    private readonly DataGridView _frameGrid = new();
    private readonly Button _startSimulatorButton = new();
    private readonly Button _stopSimulatorButton = new();
    private readonly Button _connectButton = new();
    private readonly Button _disconnectButton = new();

    public MainForm(CommandLineOptions options)
    {
        _options = options;
        var configPath = options.DeviceConfigPath ?? AppPaths.Resolve(Path.Combine("config", "devices.json"));
        try
        {
            _config = ConfigLoader.LoadDevice(configPath);
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
            _config.Device.Reconnect, Log);

        BuildUi();
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        Shown += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(ConfigWarning)) Log(ConfigWarning);
            if (_options.AutoStart || _options.Demo || _options.IsSmoke)
            {
                await StartSimulatorAsync();
                await ConnectAsync();
            }
            if (_options.IsSmoke)
                StartSmokeTimer(_options.SmokeSeconds!.Value);
        };
    }

    private string? ConfigWarning { get; }

    private void BuildUi()
    {
        Text = "V2 连上设备 · Modbus TCP SCADA 教学 Demo";
        Width = 1320;
        Height = 860;
        MinimumSize = new Size(1120, 740);
        StartPosition = FormStartPosition.CenterScreen;
        IndustrialTheme.StyleForm(this);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = IndustrialTheme.Background,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 238));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildCards(), 0, 1);
        root.Controls.Add(BuildStatsStrip(), 0, 2);
        root.Controls.Add(BuildBottom(), 0, 3);
        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = IndustrialTheme.Panel, Padding = new Padding(18, 8, 18, 8) };
        var title = new Label
        {
            Text = "V2 · 连上设备（Modbus TCP）",
            Font = IndustrialTheme.FontTitle,
            ForeColor = IndustrialTheme.Text,
            AutoSize = true,
            Location = new Point(18, 8),
        };
        var subtitle = new Label
        {
            Text = "MBAP + PDU → 功能码 01/03/05/06 → 寄存器 → 工程值；右侧可看原始报文。",
            Font = IndustrialTheme.FontSmall,
            ForeColor = IndustrialTheme.Muted,
            AutoSize = true,
            Location = new Point(20, 42),
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 760,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
            BackColor = Color.Transparent,
        };

        _statusLamp.Size = new Size(20, 20);
        _statusLamp.Margin = new Padding(0, 8, 6, 0);
        _statusLabel.Text = "未连接";
        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = IndustrialTheme.Muted;
        _statusLabel.Margin = new Padding(0, 10, 14, 0);

        _startSimulatorButton.Text = "启动模拟 PLC";
        _startSimulatorButton.Size = new Size(118, 34);
        _startSimulatorButton.Click += async (_, _) => await StartSimulatorAsync();
        IndustrialTheme.StyleButton(_startSimulatorButton, primary: true);

        _stopSimulatorButton.Text = "停止模拟（断线）";
        _stopSimulatorButton.Size = new Size(128, 34);
        _stopSimulatorButton.Enabled = false;
        _stopSimulatorButton.Click += async (_, _) => await StopSimulatorAsync();
        IndustrialTheme.StyleButton(_stopSimulatorButton);

        _connectButton.Text = "连接并采集";
        _connectButton.Size = new Size(108, 34);
        _connectButton.Click += async (_, _) => await ConnectAsync();
        IndustrialTheme.StyleButton(_connectButton, primary: true);

        _disconnectButton.Text = "断开";
        _disconnectButton.Size = new Size(72, 34);
        _disconnectButton.Enabled = false;
        _disconnectButton.Click += async (_, _) => await DisconnectAsync();
        IndustrialTheme.StyleButton(_disconnectButton);

        actions.Controls.Add(_statusLamp);
        actions.Controls.Add(_statusLabel);
        actions.Controls.Add(_startSimulatorButton);
        actions.Controls.Add(_stopSimulatorButton);
        actions.Controls.Add(_connectButton);
        actions.Controls.Add(_disconnectButton);

        panel.Controls.Add(actions);
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        return panel;
    }

    private Control BuildCards()
    {
        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Padding = new Padding(12, 8, 12, 0),
            BackColor = IndustrialTheme.Background,
        };
        for (var i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        for (var i = 0; i < _tags.Count; i++)
        {
            var card = new TagCard(_tags[i]) { Dock = DockStyle.Fill };
            _cards[_tags[i].Id] = card;
            cards.Controls.Add(card, i % 4, i / 4);
        }
        return cards;
    }

    private Control BuildStatsStrip()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = IndustrialTheme.Panel, Padding = new Padding(16, 4, 16, 4) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
        flow.Controls.Add(Caption("目标"));
        _targetLabel.Text = $"{_config.Device.Host}:{_config.Device.Port}  Unit={_config.Device.UnitId}  {_config.Device.ScanIntervalMs} ms";
        _targetLabel.AutoSize = true;
        _targetLabel.ForeColor = IndustrialTheme.Text;
        _targetLabel.Margin = new Padding(0, 6, 24, 0);
        flow.Controls.Add(_targetLabel);

        AddStat(flow, "采集", _cyclesLabel);
        AddStat(flow, "成功率", _successLabel);
        AddStat(flow, "延迟", _latencyLabel);
        AddStat(flow, "报文", _frameCountLabel);
        AddStat(flow, "错误", _lastErrorLabel);
        panel.Controls.Add(flow);
        return panel;
    }

    private static void AddStat(FlowLayoutPanel flow, string caption, Label value)
    {
        flow.Controls.Add(Caption(caption));
        value.Text = "—";
        value.AutoSize = true;
        value.ForeColor = IndustrialTheme.Text;
        value.Margin = new Padding(0, 6, 20, 0);
        flow.Controls.Add(value);
    }

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = IndustrialTheme.Muted,
        Font = IndustrialTheme.FontSmall,
        Margin = new Padding(0, 8, 6, 0),
    };

    private Control BuildBottom()
    {
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = IndustrialTheme.Background,
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = IndustrialTheme.Background };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));

        var tagPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 0), BackColor = IndustrialTheme.Background };
        var tagTitle = new Label { Text = "点位表（原始值 / 工程值 / 质量）", Dock = DockStyle.Top, Height = 24, ForeColor = IndustrialTheme.Accent, Font = IndustrialTheme.FontSmall };
        _tagGrid.Dock = DockStyle.Fill;
        IndustrialTheme.StyleGrid(_tagGrid);
        foreach (var tag in _tags)
        {
            var row = TagRow.From(tag);
            _rowMap[tag.Id] = row;
            _rows.Add(row);
        }
        GridColumns.ApplyTagColumns(_tagGrid);
        _tagGrid.DataSource = _rows;
        tagPanel.Controls.Add(_tagGrid);
        tagPanel.Controls.Add(tagTitle);

        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0), BackColor = IndustrialTheme.Background };
        var logTitle = new Label { Text = "运行日志", Dock = DockStyle.Top, Height = 24, ForeColor = IndustrialTheme.Accent, Font = IndustrialTheme.FontSmall };
        _logList.Dock = DockStyle.Fill;
        _logList.BackColor = IndustrialTheme.PanelAlt;
        _logList.ForeColor = IndustrialTheme.Text;
        _logList.BorderStyle = BorderStyle.None;
        _logList.Font = IndustrialTheme.FontMono;
        _logList.IntegralHeight = false;
        logPanel.Controls.Add(_logList);
        logPanel.Controls.Add(logTitle);

        left.Controls.Add(tagPanel, 0, 0);
        left.Controls.Add(logPanel, 0, 1);

        var framePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0), BackColor = IndustrialTheme.Background };
        var frameTitle = new Label
        {
            Text = "Modbus 报文（TID / FC / 地址 / 数量 / 十六进制）",
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = IndustrialTheme.Accent,
            Font = IndustrialTheme.FontSmall,
        };
        _frameGrid.Dock = DockStyle.Fill;
        IndustrialTheme.StyleGrid(_frameGrid);
        GridColumns.ApplyFrameColumns(_frameGrid);
        _frameGrid.DataSource = _frames;
        _frameGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _frameGrid.RowTemplate.Height = 22;
        framePanel.Controls.Add(_frameGrid);
        framePanel.Controls.Add(frameTitle);

        split.Controls.Add(left, 0, 0);
        split.Controls.Add(framePanel, 1, 0);
        return split;
    }

    private async Task StartSimulatorAsync()
    {
        if (_simulator is { IsRunning: true }) return;
        try
        {
            // 这个对象 = 反应釜工艺模型 + 真正的 Modbus TCP 从站（监听 15020）。
            // 它不是“假数据 API”，而是走和真实 PLC 一样的 TCP 帧。
            _simulator = new PlcSimulatorHost();
            await _simulator.StartAsync(_config.Device.Port, _config.Device.UnitId);
            _startSimulatorButton.Enabled = false;
            _stopSimulatorButton.Enabled = true;
            _statusLabel.Text = "模拟 PLC 在线";
            _statusLabel.ForeColor = IndustrialTheme.Success;
            Log($"模拟 PLC 已启动：127.0.0.1:{_simulator.BoundPort}");
        }
        catch (Exception ex)
        {
            _startSimulatorButton.Enabled = true;
            _stopSimulatorButton.Enabled = false;
            Log($"模拟 PLC 启动失败：{ex.Message}");
            MessageBox.Show(this, $"端口 {_config.Device.Port} 启动失败：{ex.Message}", "模拟 PLC",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 停止模拟 PLC 不是“停止采集”，而是“把设备从网络上拔掉”。
    /// 这样可以观察采集引擎的 Bad 质量、指数退避重连和自动恢复。
    /// </summary>
    private async Task StopSimulatorAsync()
    {
        if (_simulator is null) return;
        await _simulator.StopAsync();
        await _simulator.DisposeAsync();
        _simulator = null;
        _startSimulatorButton.Enabled = true;
        _stopSimulatorButton.Enabled = false;
        _statusLabel.Text = "模拟 PLC 已停止（观察重连）";
        _statusLabel.ForeColor = IndustrialTheme.Warning;
        Log("已停止模拟 PLC：浏览器应看到坏质量与指数退避重连。");
    }

    private async Task ConnectAsync()
    {
        if (_engine.IsRunning) return;
        await _engine.StartAsync();
        _connectButton.Enabled = false;
        _disconnectButton.Enabled = true;
        Log("采集已启动，等待 Modbus 连接。");
    }

    private async Task DisconnectAsync()
    {
        await _engine.StopAsync();
        _connectButton.Enabled = true;
        _disconnectButton.Enabled = false;
        _statusLabel.Text = "已断开";
        _statusLabel.ForeColor = IndustrialTheme.Muted;
        _statusLamp.IsOn = false;
        _statusLamp.IsFault = false;
        Log("采集与连接已停止。");
    }

    private void OnFrame(ModbusFrame frame)
    {
        if (IsDisposed) return;

        // 帧事件来自采集/协议线程，不能直接改 DataGridView/控件；
        // BeginInvoke 把“加一行报文”切回 UI 线程执行。
        if (InvokeRequired)
        {
            BeginInvoke(new Action<ModbusFrame>(OnFrame), frame);
            return;
        }

        _frames.Insert(0, new FrameRow
        {
            Time = frame.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
            Direction = frame.Direction == ModbusDirection.Sent ? "TX" : "RX",
            Hex = frame.Hex,
            Summary = frame.Summary,
        });
        while (_frames.Count > 400) _frames.RemoveAt(_frames.Count - 1);
        _frameCountLabel.Text = _frames.Count.ToString();
    }

    private void RefreshUi()
    {
        foreach (var tag in _tags)
        {
            var value = _store.Get(tag.Id);
            if (_cards.TryGetValue(tag.Id, out var card)) card.UpdateValue(value);
            if (_rowMap.TryGetValue(tag.Id, out var row)) row.Update(value, tag);
        }

        var stats = _engine.Stats;
        _cyclesLabel.Text = stats.Cycles.ToString();
        _successLabel.Text = $"{stats.SuccessRate:F1}%";
        _latencyLabel.Text = $"{stats.LastLatencyMs:F1} ms";
        _lastErrorLabel.Text = string.IsNullOrEmpty(stats.LastError) ? "—" : stats.LastError;
        _lastErrorLabel.ForeColor = string.IsNullOrEmpty(stats.LastError) ? IndustrialTheme.Muted : IndustrialTheme.Danger;

        // 顶部状态灯看的是“设备连接状态”，不是“采集任务是否启动”。
        // 采集在跑但设备断了，灯应该是黄色/故障，而不是绿色。
        var online = _session.State == DeviceConnectionState.Online;
        _statusLamp.IsOn = online;
        _statusLamp.IsFault = _session.State == DeviceConnectionState.Faulted;
        if (_engine.IsRunning)
        {
            _statusLabel.Text = online ? "已连接 · 采集中" : _session.State == DeviceConnectionState.Faulted ? "通信故障 · 重连中" : "连接中…";
            _statusLabel.ForeColor = online ? IndustrialTheme.Success : IndustrialTheme.Warning;
        }
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(Log), message);
            return;
        }

        _logList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (_logList.Items.Count > 200) _logList.Items.RemoveAt(_logList.Items.Count - 1);
    }

    private void StartSmokeTimer(int seconds)
    {
        _smokeTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1000, seconds * 1000) };
        _smokeTimer.Tick += async (_, _) =>
        {
            _smokeTimer.Stop();
            var stats = _engine.Stats;
            var quality = _store.Get("level").Quality;
            var frames = _frames.Count;
            await DisconnectAsync();
            if (_simulator is not null) await StopSimulatorAsync();

            var success = stats.Successes >= 2 && quality == TagQuality.Good && frames >= 2;
            var report = SmokeReport.Write("Scada.WinForms.V2.Modbus", success, new Dictionary<string, object?>
            {
                ["cycles"] = stats.Cycles,
                ["successes"] = stats.Successes,
                ["failures"] = stats.Failures,
                ["frames"] = frames,
                ["level_quality"] = quality.ToString(),
            });
            Log($"冒烟结果：{(success ? "PASS" : "FAIL")}，报告 {report}");
            Environment.ExitCode = success ? 0 : 1;
            Close();
        };
        _smokeTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _uiTimer.Stop();
        _smokeTimer?.Stop();
        _ = DisposeAsync();
        base.OnFormClosed(e);
    }

    private async Task DisposeAsync()
    {
        try { await _engine.DisposeAsync(); } catch { }
        if (_simulator is not null)
        {
            try { await _simulator.DisposeAsync(); } catch { }
            _simulator = null;
        }
    }
}
