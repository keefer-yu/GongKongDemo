using System.ComponentModel;
using System.Text;
using Scada.Alarms;
using Scada.Core.Acquisition;
using Scada.Core.Configuration;
using Scada.Core.Demo;
using Scada.Core.Devices;
using Scada.Core.Diagnostics;
using Scada.Core.Tags;
using Scada.Protocol.Modbus;
using Scada.Storage;
using Scada.UI.WinForms.Controls;
using Scada.UI.WinForms.Theme;
using Scada.UI.WinForms.ViewModels;

namespace Scada.WinForms.V3.AlarmHistory;

/// <summary>
/// V3：在 V2 的真实采集上增加“数据可信”能力。
/// 报警状态机 + SQLite 历史 + 趋势 + 故障注入 + 确认。
/// </summary>
public sealed class MainForm : Form
{
    private readonly CommandLineOptions _options;
    private readonly DeviceConfig _config;
    private readonly IReadOnlyList<TagDefinition> _tags;
    private readonly TagStore _store;
    private readonly ModbusTcpDeviceSession _session;
    private readonly AcquisitionEngine _engine;
    private readonly AlarmEngine _alarms;
    private readonly SqliteHistoryStore _history;
    private readonly Dictionary<string, TagCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly BindingList<TagRow> _rows = [];
    private readonly Dictionary<string, TagRow> _rowMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly BindingList<AlarmRow> _activeAlarmRows = [];
    private readonly BindingList<AlarmRow> _alarmHistoryRows = [];
    private readonly BindingList<HistoryRow> _historyRows = [];
    private readonly List<HistoryPoint> _lastQuery = [];
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 300 };
    private System.Windows.Forms.Timer? _smokeTimer;
    private System.Windows.Forms.Timer? _faultTimer;
    private PlcSimulatorHost? _simulator;
    private int _uiTicks;

    private readonly StatusLamp _statusLamp = new();
    private readonly Label _statusLabel = new();
    private readonly Label _cyclesLabel = new();
    private readonly Label _successLabel = new();
    private readonly Label _latencyLabel = new();
    private readonly Label _dbCountLabel = new();
    private readonly Label _activeCountLabel = new();
    private readonly Label _alarmBanner = new();
    private readonly Label _targetLabel = new();
    private readonly Label _historyStatsLabel = new();
    private readonly DataGridView _tagGrid = new();
    private readonly DataGridView _activeAlarmGrid = new();
    private readonly DataGridView _alarmHistoryGrid = new();
    private readonly DataGridView _historyGrid = new();
    private readonly ListBox _logList = new();
    private readonly TrendChart _trend = new();
    private readonly DarkTabControl _tabs = new();
    private readonly ComboBox _historyTagCombo = new();
    private readonly ComboBox _historyRangeCombo = new();
    private readonly Button _startSimulatorButton = new();
    private readonly Button _stopSimulatorButton = new();
    private readonly Button _connectButton = new();
    private readonly Button _disconnectButton = new();
    private readonly Button _ackButton = new();

    public MainForm(CommandLineOptions options)
    {
        _options = options;
        var configPath = options.DeviceConfigPath ?? AppPaths.Resolve(Path.Combine("config", "devices.json"));
        try { _config = ConfigLoader.LoadDevice(configPath); }
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
            _config.Device.Reconnect, Log);
        _alarms = new AlarmEngine(_tags);
        _history = new SqliteHistoryStore(AppPaths.DataFile("v3-history.db"));

        _alarms.Raised += OnAlarmRaised;
        _alarms.Cleared += OnAlarmChanged;
        _alarms.Acknowledged += OnAlarmChanged;
        _engine.StatsChanged += OnStatsChanged;

        BuildUi();
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        Shown += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(ConfigWarning)) Log(ConfigWarning);
            try { await _history.InitializeAsync(); }
            catch (Exception ex) { Log($"历史库初始化失败：{ex.Message}"); }

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
        Text = "V3 数据可信 · 报警 / 历史 / 趋势";
        Width = 1360;
        Height = 900;
        MinimumSize = new Size(1160, 780);
        StartPosition = FormStartPosition.CenterScreen;
        IndustrialTheme.StyleForm(this);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = IndustrialTheme.Background };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildCards(), 0, 1);
        root.Controls.Add(BuildStatsStrip(), 0, 2);
        root.Controls.Add(BuildTabs(), 0, 3);
        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = IndustrialTheme.Panel, Padding = new Padding(18, 8, 18, 8) };
        panel.Controls.Add(new Label
        {
            Text = "V3 · 数据可信（报警 / 历史 / 趋势）",
            Font = IndustrialTheme.FontTitle,
            ForeColor = IndustrialTheme.Text,
            AutoSize = true,
            Location = new Point(18, 8),
        });
        panel.Controls.Add(new Label
        {
            Text = "采集 → 质量码 → 四限报警（死区/延时/确认）→ SQLite 历史 → 趋势；注入故障看报警如何产生。",
            Font = IndustrialTheme.FontSmall,
            ForeColor = IndustrialTheme.Muted,
            AutoSize = true,
            Location = new Point(20, 42),
        });

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
        _statusLabel.Margin = new Padding(0, 10, 12, 0);

        _startSimulatorButton.Text = "启动模拟 PLC";
        _startSimulatorButton.Size = new Size(116, 34);
        _startSimulatorButton.Click += async (_, _) => await StartSimulatorAsync();
        IndustrialTheme.StyleButton(_startSimulatorButton, primary: true);

        _stopSimulatorButton.Text = "停止模拟（断线）";
        _stopSimulatorButton.Size = new Size(126, 34);
        _stopSimulatorButton.Enabled = false;
        _stopSimulatorButton.Click += async (_, _) => await StopSimulatorAsync();
        IndustrialTheme.StyleButton(_stopSimulatorButton);

        _connectButton.Text = "连接并采集";
        _connectButton.Size = new Size(106, 34);
        _connectButton.Click += async (_, _) => await ConnectAsync();
        IndustrialTheme.StyleButton(_connectButton, primary: true);

        _disconnectButton.Text = "断开";
        _disconnectButton.Size = new Size(70, 34);
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
        return panel;
    }

    private Control BuildCards()
    {
        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Padding = new Padding(12, 6, 12, 0),
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
        _targetLabel.Text = $"{_config.Device.Host}:{_config.Device.Port}  Unit={_config.Device.UnitId}";
        _targetLabel.AutoSize = true;
        _targetLabel.Margin = new Padding(0, 6, 20, 0);
        flow.Controls.Add(_targetLabel);
        AddStat(flow, "采集", _cyclesLabel);
        AddStat(flow, "成功率", _successLabel);
        AddStat(flow, "延迟", _latencyLabel);
        AddStat(flow, "历史行数", _dbCountLabel);
        AddStat(flow, "活动报警", _activeCountLabel);
        panel.Controls.Add(flow);
        return panel;
    }

    private Control BuildTabs()
    {
        _tabs.Dock = DockStyle.Fill;
        _tabs.Padding = new Point(12, 6);

        var realtime = new TabPage("实时") { BackColor = IndustrialTheme.Background, Padding = new Padding(6) };
        realtime.Controls.Add(BuildRealtimeTab());

        var alarms = new TabPage("报警") { BackColor = IndustrialTheme.Background, Padding = new Padding(6) };
        alarms.Controls.Add(BuildAlarmTab());

        var history = new TabPage("历史") { BackColor = IndustrialTheme.Background, Padding = new Padding(6) };
        history.Controls.Add(BuildHistoryTab());

        _tabs.TabPages.Add(realtime);
        _tabs.TabPages.Add(alarms);
        _tabs.TabPages.Add(history);
        return _tabs;
    }

    private Control BuildRealtimeTab()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            ColumnStyles = { new ColumnStyle(SizeType.Percent, 56), new ColumnStyle(SizeType.Percent, 44) },
        };
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var tags = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 0) };
        var tagTitle = new Label { Text = "点位表", Dock = DockStyle.Top, Height = 24, ForeColor = IndustrialTheme.Accent, Font = IndustrialTheme.FontSmall };
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
        tags.Controls.Add(_tagGrid);
        tags.Controls.Add(tagTitle);

        var trendPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
        var trendTitle = new Label { Text = "实时趋势（液位 / 温度）", Dock = DockStyle.Top, Height = 24, ForeColor = IndustrialTheme.Accent, Font = IndustrialTheme.FontSmall };
        _trend.Dock = DockStyle.Fill;
        _trend.SetRange(0, 150);
        _trend.AddSeries("液位", IndustrialTheme.Accent);
        _trend.AddSeries("温度", IndustrialTheme.Warning);
        trendPanel.Controls.Add(_trend);
        trendPanel.Controls.Add(trendTitle);

        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
        var logTitle = new Label { Text = "运行日志", Dock = DockStyle.Top, Height = 24, ForeColor = IndustrialTheme.Accent, Font = IndustrialTheme.FontSmall };
        _logList.Dock = DockStyle.Fill;
        _logList.BackColor = IndustrialTheme.PanelAlt;
        _logList.ForeColor = IndustrialTheme.Text;
        _logList.BorderStyle = BorderStyle.None;
        _logList.Font = IndustrialTheme.FontMono;
        _logList.IntegralHeight = false;
        logPanel.Controls.Add(_logList);
        logPanel.Controls.Add(logTitle);

        // 右侧上：趋势；右侧下：日志。
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 72));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
        right.Controls.Add(trendPanel, 0, 0);
        right.Controls.Add(logPanel, 0, 1);

        grid.Controls.Add(tags, 0, 0);
        grid.Controls.Add(right, 1, 0);
        return grid;
    }

    private Control BuildAlarmTab()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _alarmBanner.Dock = DockStyle.Fill;
        _alarmBanner.Text = "无活动报警";
        _alarmBanner.Font = IndustrialTheme.FontNormal;
        _alarmBanner.ForeColor = IndustrialTheme.Success;
        _alarmBanner.TextAlign = ContentAlignment.MiddleLeft;
        _alarmBanner.Padding = new Padding(12, 0, 0, 0);
        _alarmBanner.BackColor = IndustrialTheme.Panel;
        layout.Controls.Add(_alarmBanner, 0, 0);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = IndustrialTheme.Panel, Padding = new Padding(8, 5, 0, 0) };
        _ackButton.Text = "确认选中报警";
        _ackButton.Size = new Size(112, 30);
        _ackButton.Click += (_, _) => AcknowledgeSelected();
        IndustrialTheme.StyleButton(_ackButton);
        var ackAll = new Button { Text = "确认全部", Size = new Size(88, 30) };
        ackAll.Click += (_, _) => AcknowledgeAll();
        IndustrialTheme.StyleButton(ackAll);
        var faultTemp = new Button { Text = "注入超温故障", Size = new Size(108, 30) };
        faultTemp.Click += (_, _) => InjectFault(FaultKind.HighTemperature, "超温");
        IndustrialTheme.StyleButton(faultTemp);
        var faultPressure = new Button { Text = "注入超压故障", Size = new Size(108, 30) };
        faultPressure.Click += (_, _) => InjectFault(FaultKind.HighPressure, "超压");
        IndustrialTheme.StyleButton(faultPressure);
        var faultLevel = new Button { Text = "注入低液位", Size = new Size(96, 30) };
        faultLevel.Click += (_, _) => InjectFault(FaultKind.LowLevel, "低液位");
        IndustrialTheme.StyleButton(faultLevel);
        toolbar.Controls.AddRange([_ackButton, ackAll, faultTemp, faultPressure, faultLevel]);
        layout.Controls.Add(toolbar, 0, 1);

        var grids = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        grids.RowStyles.Add(new RowStyle(SizeType.Percent, 46));
        grids.RowStyles.Add(new RowStyle(SizeType.Percent, 54));

        var activePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 6) };
        var activeTitle = new Label { Text = "活动报警（未确认 / 已确认）", Dock = DockStyle.Top, Height = 24, ForeColor = IndustrialTheme.Accent, Font = IndustrialTheme.FontSmall };
        IndustrialTheme.StyleGrid(_activeAlarmGrid);
        _activeAlarmGrid.Dock = DockStyle.Fill;
        _activeAlarmGrid.DataSource = _activeAlarmRows;
        AddAlarmColumns(_activeAlarmGrid);
        activePanel.Controls.Add(_activeAlarmGrid);
        activePanel.Controls.Add(activeTitle);

        var historyPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
        var historyTitle = new Label { Text = "报警历史（本次运行）", Dock = DockStyle.Top, Height = 24, ForeColor = IndustrialTheme.Accent, Font = IndustrialTheme.FontSmall };
        IndustrialTheme.StyleGrid(_alarmHistoryGrid);
        _alarmHistoryGrid.Dock = DockStyle.Fill;
        _alarmHistoryGrid.DataSource = _alarmHistoryRows;
        AddAlarmColumns(_alarmHistoryGrid);
        historyPanel.Controls.Add(_alarmHistoryGrid);
        historyPanel.Controls.Add(historyTitle);

        grids.Controls.Add(activePanel, 0, 0);
        grids.Controls.Add(historyPanel, 0, 1);
        layout.Controls.Add(grids, 0, 2);
        return layout;
    }

    private Control BuildHistoryTab()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = IndustrialTheme.Panel, Padding = new Padding(8, 6, 0, 0) };
        toolbar.Controls.Add(Caption("点位"));
        _historyTagCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _historyTagCombo.Width = 150;
        foreach (var tag in _tags) _historyTagCombo.Items.Add($"{tag.Name} ({tag.Id})");
        _historyTagCombo.SelectedIndex = 1; // temp
        toolbar.Controls.Add(_historyTagCombo);

        toolbar.Controls.Add(Caption("时间范围"));
        _historyRangeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _historyRangeCombo.Width = 100;
        _historyRangeCombo.Items.AddRange(["最近 1 分钟", "最近 5 分钟", "最近 30 分钟"]);
        _historyRangeCombo.SelectedIndex = 1;
        toolbar.Controls.Add(_historyRangeCombo);

        var query = new Button { Text = "查询历史", Size = new Size(90, 30) };
        query.Click += async (_, _) => await QueryHistoryAsync();
        IndustrialTheme.StyleButton(query, primary: true);
        toolbar.Controls.Add(query);

        var export = new Button { Text = "导出 CSV", Size = new Size(90, 30) };
        export.Click += (_, _) => ExportCsv();
        IndustrialTheme.StyleButton(export);
        toolbar.Controls.Add(export);

        var cleanup = new Button { Text = "清理 7 天前", Size = new Size(104, 30) };
        cleanup.Click += async (_, _) => await PruneAsync();
        IndustrialTheme.StyleButton(cleanup);
        toolbar.Controls.Add(cleanup);
        layout.Controls.Add(toolbar, 0, 0);

        _historyStatsLabel.Text = "等待查询…";
        _historyStatsLabel.Dock = DockStyle.Fill;
        _historyStatsLabel.ForeColor = IndustrialTheme.Muted;
        _historyStatsLabel.Font = IndustrialTheme.FontSmall;
        _historyStatsLabel.Padding = new Padding(10, 2, 0, 0);
        layout.Controls.Add(_historyStatsLabel, 0, 1);

        IndustrialTheme.StyleGrid(_historyGrid);
        _historyGrid.Dock = DockStyle.Fill;
        _historyGrid.DataSource = _historyRows;
        _historyGrid.Columns.Clear();
        _historyGrid.AutoGenerateColumns = false;
        _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(HistoryRow.Time), HeaderText = "时间", FillWeight = 30 });
        _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(HistoryRow.TagId), HeaderText = "点位", FillWeight = 20 });
        _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(HistoryRow.Value), HeaderText = "工程值", FillWeight = 20 });
        _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(HistoryRow.Quality), HeaderText = "质量", FillWeight = 15 });
        layout.Controls.Add(_historyGrid, 0, 2);
        return layout;
    }

    private static void AddAlarmColumns(DataGridView grid)
    {
        grid.AutoGenerateColumns = false;
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AlarmRow.Time), HeaderText = "时间", FillWeight = 26 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AlarmRow.Level), HeaderText = "级别", FillWeight = 16 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AlarmRow.TagName), HeaderText = "点位", FillWeight = 16 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AlarmRow.Message), HeaderText = "消息", FillWeight = 52 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(AlarmRow.State), HeaderText = "状态", FillWeight = 16 });
    }

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = IndustrialTheme.Muted,
        Font = IndustrialTheme.FontSmall,
        Margin = new Padding(0, 8, 6, 0),
    };

    private static void AddStat(FlowLayoutPanel flow, string caption, Label value)
    {
        flow.Controls.Add(Caption(caption));
        value.Text = "—";
        value.AutoSize = true;
        value.Margin = new Padding(0, 6, 18, 0);
        flow.Controls.Add(value);
    }

    private void SetHeader(DataGridView grid, string name, string header, bool visible = true)
    {
        if (grid.Columns[name] is not { } column) return;
        column.HeaderText = header;
        column.Visible = visible;
    }

    private async Task StartSimulatorAsync()
    {
        if (_simulator is { IsRunning: true }) return;
        try
        {
            _simulator = new PlcSimulatorHost();
            await _simulator.StartAsync(_config.Device.Port, _config.Device.UnitId);
            _startSimulatorButton.Enabled = false;
            _stopSimulatorButton.Enabled = true;
            Log($"模拟 PLC 已启动：127.0.0.1:{_simulator.BoundPort}");
        }
        catch (Exception ex)
        {
            Log($"模拟 PLC 启动失败：{ex.Message}");
            MessageBox.Show(this, $"端口 {_config.Device.Port} 启动失败：{ex.Message}", "模拟 PLC",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task StopSimulatorAsync()
    {
        if (_simulator is null) return;
        await _simulator.DisposeAsync();
        _simulator = null;
        _startSimulatorButton.Enabled = true;
        _stopSimulatorButton.Enabled = false;
        Log("模拟 PLC 已停止：观察坏质量与重连。");
    }

    private async Task ConnectAsync()
    {
        if (_engine.IsRunning) return;
        await _engine.StartAsync();
        _connectButton.Enabled = false;
        _disconnectButton.Enabled = true;
        Log("采集已启动。");
    }

    private async Task DisconnectAsync()
    {
        await _engine.StopAsync();
        _connectButton.Enabled = true;
        _disconnectButton.Enabled = false;
        _statusLamp.IsOn = false;
        _statusLamp.IsFault = false;
        _statusLabel.Text = "已断开";
        Log("采集已停止。");
    }

    /// <summary>演示用：直接改模拟器内部状态，制造超温/超压/低液位。</summary>
    private void InjectFault(FaultKind kind, string name)
    {
        if (_simulator is null)
        {
            MessageBox.Show(this, "内置模拟器未启动；真实 PLC 无法注入故障。", "故障注入",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 20 秒后自动恢复，方便反复演示。
        _simulator.Model.InjectFault(kind, TimeSpan.FromSeconds(20));
        Log($"已注入故障：{name}，20 秒后自动恢复。");
    }

    /// <summary>
    /// 每次采集成功后触发：先评估报警，再把这一轮快照写历史。
    /// 注意这是后台线程，不能直接改 UI；报警事件内部会 BeginInvoke。
    /// </summary>
    private void OnStatsChanged(AcquisitionStatistics stats)
    {
        // 刚启动/失败次数为 0 时没有有效快照，跳过。
        if (stats.Successes == 0) return;

        // 用“这一轮”的快照同时评估报警和写历史，保证两边看到的是同一份数据。
        var snapshot = _store.Snapshot();
        _alarms.Evaluate(snapshot);

        // 只有本轮没有通信错误才写历史，避免把 Bad 值当正常数据落库。
        if (stats.LastError is null)
            _ = PersistSnapshotAsync(snapshot);
    }

    private async Task PersistSnapshotAsync(IReadOnlyDictionary<string, TagValue> snapshot)
    {
        try
        {
            await _history.WriteTagRangeAsync(snapshot.Values).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"写历史失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 报警事件来自 AlarmEngine（采集线程），这里做两件事：
    /// 1. 异步落 SQLite；2. 切回 UI 线程刷新报警列表。
    /// </summary>
    private void OnAlarmRaised(AlarmRecord record)
    {
        _ = _history.WriteAlarmAsync(record);
        Log($"[{record.Level}] {record.Message}");
        RefreshAlarmsSafe();
    }

    private void OnAlarmChanged(AlarmRecord record)
    {
        // 确认/恢复也用同一个记录，状态会更新，所以再写一次历史（形成完整生命周期）。
        _ = _history.WriteAlarmAsync(record);
        Log($"[{record.State}] {record.TagName} {record.Message}");
        RefreshAlarmsSafe();
    }

    private void RefreshAlarmsSafe()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(RefreshAlarms));
            return;
        }
        RefreshAlarms();
    }

    private void RefreshAlarms()
    {
        _activeAlarmRows.Clear();
        foreach (var record in _alarms.Active)
            _activeAlarmRows.Add(AlarmRow.From(record));

        _alarmHistoryRows.Clear();
        foreach (var record in _alarms.Recent.OrderByDescending(r => r.RaisedAt))
            _alarmHistoryRows.Add(AlarmRow.From(record));

        var active = _alarms.Active;
        if (active.Count == 0)
        {
            _alarmBanner.Text = "✓ 无活动报警";
            _alarmBanner.ForeColor = IndustrialTheme.Success;
        }
        else
        {
            var highest = active.OrderByDescending(a => a.Level).First();
            _alarmBanner.Text = $"⚠ {active.Count} 条活动报警 · 最高 {highest.Level}：{highest.Message}";
            _alarmBanner.ForeColor = highest.Level is AlarmLevel.HH or AlarmLevel.LL
                ? IndustrialTheme.Danger : IndustrialTheme.Warning;
        }
        _activeCountLabel.Text = active.Count.ToString();
    }

    private void AcknowledgeSelected()
    {
        if (_activeAlarmGrid.CurrentRow?.DataBoundItem is not AlarmRow row) return;
        _alarms.Acknowledge(row.TagId);
        RefreshAlarms();
    }

    private void AcknowledgeAll()
    {
        foreach (var record in _alarms.Active.DistinctBy(a => a.TagId).ToArray())
            _alarms.Acknowledge(record.TagId);
        RefreshAlarms();
    }

    /// <summary>
    /// 历史查询：从 SQLite 按点位 + 时间范围取数据，转成表格行。
    /// 查询是异步的，避免历史数据多时卡住 UI 线程。
    /// </summary>
    private async Task QueryHistoryAsync()
    {
        var tag = _tags[Math.Max(0, _historyTagCombo.SelectedIndex)];
        var minutes = _historyRangeCombo.SelectedIndex switch { 0 => 1, 2 => 30, _ => 5 };
        var to = DateTimeOffset.UtcNow;
        var from = to.AddMinutes(-minutes);
        try
        {
            var rows = await _history.QueryHistoryAsync(tag.Id, from, to, 2000);
            _lastQuery.Clear();
            _historyRows.Clear();
            foreach (var row in rows)
            {
                _lastQuery.Add(row);
                _historyRows.Add(HistoryRow.From(row));
            }
            _historyStatsLabel.Text = $"{tag.Name} · 最近 {minutes} 分钟 · {rows.Count} 行 · {from.ToLocalTime():HH:mm:ss} → {to.ToLocalTime():HH:mm:ss}";
            _dbCountLabel.Text = (await _history.CountHistoryAsync()).ToString();
        }
        catch (Exception ex)
        {
            _historyStatsLabel.Text = $"查询失败：{ex.Message}";
        }
    }

    private void ExportCsv()
    {
        if (_lastQuery.Count == 0)
        {
            MessageBox.Show(this, "请先查询历史数据。", "导出 CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var tag = _tags[Math.Max(0, _historyTagCombo.SelectedIndex)];
        var path = AppPaths.DataFile($"history-{tag.Id}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var sb = new StringBuilder();
        sb.AppendLine("time,tag,value,quality,raw");
        foreach (var row in _lastQuery)
            sb.AppendLine($"{row.Timestamp:O},{row.TagId},{row.Value},{row.Quality},{row.RawValue}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        MessageBox.Show(this, $"已导出 {_lastQuery.Count} 行到：\n{path}", "导出 CSV",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task PruneAsync()
    {
        try
        {
            var deleted = await _history.PruneAsync(DateTimeOffset.UtcNow.AddDays(-7));
            Log($"已清理 {deleted} 行 7 天前历史。");
            _dbCountLabel.Text = (await _history.CountHistoryAsync()).ToString();
        }
        catch (Exception ex)
        {
            Log($"清理失败：{ex.Message}");
        }
    }

    private void RefreshUi()
    {
        foreach (var tag in _tags)
        {
            var value = _store.Get(tag.Id);
            if (_cards.TryGetValue(tag.Id, out var card)) card.UpdateValue(value);
            if (_rowMap.TryGetValue(tag.Id, out var row)) row.Update(value, tag);
            if (value.IsGood && tag.Id.Equals("level", StringComparison.OrdinalIgnoreCase))
                _trend.AddPoint("液位", value.Value);
            if (value.IsGood && tag.Id.Equals("temp", StringComparison.OrdinalIgnoreCase))
                _trend.AddPoint("温度", value.Value);
        }

        var stats = _engine.Stats;
        _cyclesLabel.Text = stats.Cycles.ToString();
        _successLabel.Text = $"{stats.SuccessRate:F1}%";
        _latencyLabel.Text = $"{stats.LastLatencyMs:F1} ms";
        _statusLamp.IsOn = _session.State == DeviceConnectionState.Online;
        _statusLamp.IsFault = _session.State == DeviceConnectionState.Faulted;
        if (_engine.IsRunning)
        {
            _statusLabel.Text = _session.State == DeviceConnectionState.Online
                ? "已连接 · 采集中" : "通信故障 · 重连中";
            _statusLabel.ForeColor = _session.State == DeviceConnectionState.Online
                ? IndustrialTheme.Success : IndustrialTheme.Warning;
        }

        _uiTicks++;
        if (_uiTicks % 10 == 0)
            _ = UpdateDbCountAsync();
    }

    private async Task UpdateDbCountAsync()
    {
        try { _dbCountLabel.Text = (await _history.CountHistoryAsync()).ToString(); }
        catch { /* 忽略统计刷新失败 */ }
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(Log), message);
            return;
        }
        _logList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (_logList.Items.Count > 300) _logList.Items.RemoveAt(_logList.Items.Count - 1);
    }

    private void StartSmokeTimer(int seconds)
    {
        _faultTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _faultTimer.Tick += (_, _) =>
        {
            _faultTimer.Stop();
            InjectFault(FaultKind.HighTemperature, "冒烟超温");
        };
        _faultTimer.Start();

        _smokeTimer = new System.Windows.Forms.Timer { Interval = Math.Max(6000, seconds * 1000) };
        _smokeTimer.Tick += async (_, _) =>
        {
            _smokeTimer.Stop();
            _faultTimer?.Stop();

            var stats = _engine.Stats;
            var active = _alarms.Active;
            var hh = active.FirstOrDefault(a => a.Level == AlarmLevel.HH);
            if (hh is not null) _alarms.Acknowledge(hh.TagId);
            var dbRows = await _history.CountHistoryAsync();
            await DisconnectAsync();
            if (_simulator is not null) await StopSimulatorAsync();
            RefreshAlarms();

            var success = stats.Successes >= 2 && hh is not null && dbRows > 0;
            var report = SmokeReport.Write("Scada.WinForms.V3.AlarmHistory", success, new Dictionary<string, object?>
            {
                ["cycles"] = stats.Cycles,
                ["successes"] = stats.Successes,
                ["active_alarms"] = active.Count,
                ["hh_alarm"] = hh?.Message,
                ["history_rows"] = dbRows,
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
        _faultTimer?.Stop();
        _ = DisposeAllAsync();
        base.OnFormClosed(e);
    }

    private async Task DisposeAllAsync()
    {
        try { await _engine.DisposeAsync(); } catch { }
        if (_simulator is not null)
        {
            try { await _simulator.DisposeAsync(); } catch { }
        }
        try { await _history.DisposeAsync(); } catch { }
    }
}
