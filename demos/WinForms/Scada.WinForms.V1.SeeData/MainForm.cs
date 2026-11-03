using System.ComponentModel;
using Scada.Core.Acquisition;
using Scada.Core.Demo;
using Scada.Core.Devices;
using Scada.Core.Diagnostics;
using Scada.Core.Tags;
using Scada.UI.WinForms.Controls;
using Scada.UI.WinForms.Theme;
using Scada.UI.WinForms.ViewModels;

namespace Scada.WinForms.V1.SeeData;

/// <summary>
/// V1：只做一件事——把设备里的值稳定、可读地搬到界面上。
/// 没有 Modbus、没有数据库、没有控制；先把“扫描周期 → 标签库 → UI”跑通。
///
/// 这个窗体的职责只有“接线”和“显示”：
///   SimulatedDeviceSession → AcquisitionEngine → TagStore → 卡片/表格
/// 真正的采集逻辑在 Scada.Core，窗体不写协议、不写线程循环。
/// </summary>
public sealed class MainForm : Form
{
    private readonly CommandLineOptions _options;
    private readonly IReadOnlyList<TagDefinition> _tags;
    private readonly TagStore _store;
    private readonly SimulatedDeviceSession _session;
    private readonly AcquisitionEngine _engine;

    // 点位 ID → 卡片 / 表格行，刷新时按 ID 找控件，避免每次重建控件。
    private readonly Dictionary<string, TagCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly BindingList<TagRow> _rows = [];
    private readonly Dictionary<string, TagRow> _rowMap = new(StringComparer.OrdinalIgnoreCase);

    // UI 定时器只负责“拉快照 + 刷控件”，不负责采集。
    // 采集在后台 Task 里，周期可能和 UI 刷新不同（这里 500ms vs 250ms）。
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 250 };
    private System.Windows.Forms.Timer? _smokeTimer;

    private readonly StatusLamp _statusLamp = new();
    private readonly Label _statusLabel = new();
    private readonly Label _cyclesLabel = new();
    private readonly Label _successLabel = new();
    private readonly Label _latencyLabel = new();
    private readonly Label _errorLabel = new();
    private readonly Label _footerLabel = new();
    private readonly ListBox _logList = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly ComboBox _intervalCombo = new();
    private readonly DataGridView _grid = new();

    private int _selectedIntervalMs = 500;

    public MainForm(CommandLineOptions options)
    {
        _options = options;
        _tags = DemoPlant.CreateTagDefinitions();
        _store = new TagStore();
        _store.Initialize(_tags);
        _session = new SimulatedDeviceSession(DemoPlant.DeviceId, _tags);
        _engine = new AcquisitionEngine(
            _session, _tags, _store, TimeSpan.FromMilliseconds(_selectedIntervalMs),
            new ReconnectSettings { InitialDelayMs = 500, MaxDelayMs = 4000 },
            Log);

        // 先搭界面，再启动 UI 刷新；采集在 Shown 里按需启动。
        BuildUi();
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        Shown += async (_, _) =>
        {
            // 正常运行时让用户点“启动采集”；--demo/--smoke 自动启动。
            if (_options.AutoStart || _options.Demo || _options.IsSmoke)
                await StartAsync();
            if (_options.IsSmoke)
                StartSmokeTimer(_options.SmokeSeconds!.Value);
        };
    }

    private void BuildUi()
    {
        // 三行布局：顶部标题/按钮、中间卡片+表格、底部提示。
        // 用 TableLayoutPanel 而不是绝对坐标，窗口缩放时控件会跟着走。
        Text = "V1 看见数据 · SCADA 教学 Demo";
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(1080, 700);
        StartPosition = FormStartPosition.CenterScreen;
        IndustrialTheme.StyleForm(this);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = IndustrialTheme.Background,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildContent(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = IndustrialTheme.Panel, Padding = new Padding(18, 10, 18, 8) };

        var title = new Label
        {
            Text = "V1 · 看见数据",
            Font = IndustrialTheme.FontTitle,
            ForeColor = IndustrialTheme.Text,
            AutoSize = true,
            Location = new Point(18, 10),
        };
        var subtitle = new Label
        {
            Text = "内存模拟设备 → 500ms 轮询 → TagStore → WinForms；先理解扫描周期和质量码。",
            Font = IndustrialTheme.FontSmall,
            ForeColor = IndustrialTheme.Muted,
            AutoSize = true,
            Location = new Point(20, 42),
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 520,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0),
            BackColor = Color.Transparent,
        };

        _statusLamp.IsOn = false;
        _statusLamp.Size = new Size(20, 20);
        _statusLamp.Margin = new Padding(0, 8, 6, 0);
        _statusLabel.Text = "已停止";
        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = IndustrialTheme.Muted;
        _statusLabel.Margin = new Padding(0, 10, 18, 0);

        _startButton.Text = "▶ 启动采集";
        _startButton.Size = new Size(108, 34);
        _startButton.Click += async (_, _) => await StartAsync();
        IndustrialTheme.StyleButton(_startButton, primary: true);

        _stopButton.Text = "■ 停止";
        _stopButton.Size = new Size(88, 34);
        _stopButton.Enabled = false;
        _stopButton.Click += async (_, _) => await StopAsync();
        IndustrialTheme.StyleButton(_stopButton);

        _intervalCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _intervalCombo.Width = 100;
        _intervalCombo.Height = 34;
        _intervalCombo.Items.AddRange(["250 ms", "500 ms", "1000 ms"]);
        _intervalCombo.SelectedIndex = 1;
        _intervalCombo.SelectedIndexChanged += (_, _) =>
        {
            _selectedIntervalMs = _intervalCombo.SelectedIndex switch { 0 => 250, 2 => 1000, _ => 500 };
        };

        actions.Controls.Add(_statusLamp);
        actions.Controls.Add(_statusLabel);
        actions.Controls.Add(_startButton);
        actions.Controls.Add(_stopButton);
        actions.Controls.Add(new Label
        {
            Text = "周期",
            AutoSize = true,
            ForeColor = IndustrialTheme.Muted,
            Margin = new Padding(8, 10, 4, 0),
        });
        actions.Controls.Add(_intervalCombo);

        panel.Controls.Add(actions);
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        return panel;
    }

    private Control BuildContent()
    {
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(12),
            BackColor = IndustrialTheme.Background,
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        split.Controls.Add(BuildLeft(), 0, 0);
        split.Controls.Add(BuildRight(), 1, 0);
        return split;
    }

    private Control BuildLeft()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = IndustrialTheme.Background,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 242));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
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

        var gridPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5), BackColor = IndustrialTheme.Background };
        _grid.Dock = DockStyle.Fill;
        IndustrialTheme.StyleGrid(_grid);
        foreach (var tag in _tags)
        {
            var row = TagRow.From(tag);
            _rowMap[tag.Id] = row;
            _rows.Add(row);
        }
        GridColumns.ApplyTagColumns(_grid);
        _grid.DataSource = _rows;
        gridPanel.Controls.Add(_grid);

        layout.Controls.Add(cards, 0, 0);
        layout.Controls.Add(gridPanel, 0, 1);
        return layout;
    }

    private Control BuildRight()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = IndustrialTheme.Background,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 290));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var stats = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = IndustrialTheme.Panel,
            Padding = new Padding(16, 12, 16, 12),
        };
        stats.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        stats.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        stats.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var statsTitle = new Label
        {
            Text = "采集统计",
            Font = IndustrialTheme.FontNormal,
            ForeColor = IndustrialTheme.Accent,
            Dock = DockStyle.Fill,
        };
        stats.Controls.Add(statsTitle, 0, 0);

        var flow = new Label
        {
            Text = "设备（内存模拟）\n   ↓ 每 500 ms 读取\n采集引擎\n   ↓\nTagStore（实时标签库）\n   ↓\nWinForms 卡片 / 表格",
            Font = IndustrialTheme.FontSmall,
            ForeColor = IndustrialTheme.Muted,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 4, 0, 0),
        };
        stats.Controls.Add(flow, 0, 1);

        var statsTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = Color.Transparent,
        };
        statsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        statsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        for (var i = 0; i < 4; i++) statsTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        AddStatRow(statsTable, 0, "采集次数", _cyclesLabel);
        AddStatRow(statsTable, 1, "成功率", _successLabel);
        AddStatRow(statsTable, 2, "平均延迟", _latencyLabel);
        AddStatRow(statsTable, 3, "最近错误", _errorLabel);
        stats.Controls.Add(statsTable, 0, 2);

        var logPanel = new Panel { Dock = DockStyle.Fill, BackColor = IndustrialTheme.Panel, Padding = new Padding(10) };
        var logTitle = new Label
        {
            Text = "运行日志",
            ForeColor = IndustrialTheme.Accent,
            Dock = DockStyle.Top,
            Height = 24,
            Font = IndustrialTheme.FontSmall,
        };
        _logList.Dock = DockStyle.Fill;
        _logList.BackColor = IndustrialTheme.PanelAlt;
        _logList.ForeColor = IndustrialTheme.Text;
        _logList.BorderStyle = BorderStyle.None;
        _logList.Font = IndustrialTheme.FontMono;
        _logList.IntegralHeight = false;
        logPanel.Controls.Add(_logList);
        logPanel.Controls.Add(logTitle);

        layout.Controls.Add(stats, 0, 0);
        layout.Controls.Add(logPanel, 0, 1);
        return layout;
    }

    private static void AddStatRow(TableLayoutPanel table, int row, string caption, Label value)
    {
        table.Controls.Add(new Label
        {
            Text = caption,
            ForeColor = IndustrialTheme.Muted,
            Font = IndustrialTheme.FontSmall,
            Dock = DockStyle.Fill,
        }, 0, row);
        value.Text = "--";
        value.ForeColor = IndustrialTheme.Text;
        value.Font = IndustrialTheme.FontSmall;
        value.Dock = DockStyle.Fill;
        table.Controls.Add(value, 1, row);
    }

    private Control BuildFooter()
    {
        _footerLabel.Dock = DockStyle.Fill;
        _footerLabel.Text = "提示：先启动采集，观察扫描次数与延迟；再把周期改为 250 ms 看界面刷新压力。";
        _footerLabel.ForeColor = IndustrialTheme.Muted;
        _footerLabel.Font = IndustrialTheme.FontSmall;
        _footerLabel.Padding = new Padding(16, 0, 0, 0);
        _footerLabel.BackColor = IndustrialTheme.Panel;
        return _footerLabel;
    }

    private async Task StartAsync()
    {
        if (_engine.IsRunning) return;
        try
        {
            _startButton.Enabled = false;
            _stopButton.Enabled = true;
            _intervalCombo.Enabled = false;
            await _engine.StartAsync();
            _statusLabel.Text = "运行中";
            _statusLabel.ForeColor = IndustrialTheme.Success;
            _statusLamp.IsOn = true;
        }
        catch (Exception ex)
        {
            Log($"启动失败：{ex.Message}");
            _startButton.Enabled = true;
            _stopButton.Enabled = false;
            _intervalCombo.Enabled = true;
            _statusLabel.Text = "启动失败";
            _statusLabel.ForeColor = IndustrialTheme.Danger;
            _statusLamp.IsFault = true;
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await _engine.StopAsync();
        }
        catch (Exception ex)
        {
            Log($"停止失败：{ex.Message}");
        }
        _startButton.Enabled = true;
        _stopButton.Enabled = false;
        _intervalCombo.Enabled = true;
        _statusLabel.Text = "已停止";
        _statusLabel.ForeColor = IndustrialTheme.Muted;
        _statusLamp.IsOn = false;
        _statusLamp.IsFault = false;
    }

    private void RefreshUi()
    {
        // 注意：这里没有直接读设备，而是读 TagStore 的当前值。
        // 采集线程负责写 TagStore，UI 只负责“取当前快照并显示”，两边解耦。
        foreach (var tag in _tags)
        {
            var value = _store.Get(tag.Id);
            if (_cards.TryGetValue(tag.Id, out var card))
                card.UpdateValue(value);
            if (_rowMap.TryGetValue(tag.Id, out var row))
                row.Update(value, tag);
        }

        var stats = _engine.Stats;
        _cyclesLabel.Text = stats.Cycles.ToString();
        _successLabel.Text = $"{stats.SuccessRate:F1}%（成功 {stats.Successes} / 失败 {stats.Failures}）";
        _latencyLabel.Text = $"{stats.LastLatencyMs:F1} / {stats.AverageLatencyMs:F1} ms";
        _errorLabel.Text = string.IsNullOrEmpty(stats.LastError) ? "—" : stats.LastError;
        _errorLabel.ForeColor = string.IsNullOrEmpty(stats.LastError) ? IndustrialTheme.Muted : IndustrialTheme.Danger;
        _footerLabel.Text = $"设备：{DemoPlant.DeviceName}（{DemoPlant.DeviceId}）   周期：{_selectedIntervalMs} ms   " +
                            $"最近采集：{stats.LastCycleAt?.ToLocalTime():HH:mm:ss.fff}   提示：坏值显示 ERR，不是 0。";
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(Log), message);
            return;
        }

        _logList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (_logList.Items.Count > 200)
            _logList.Items.RemoveAt(_logList.Items.Count - 1);
    }

    /// <summary>
    /// 自动验收：运行 N 秒后检查“采集次数够多 + 质量 Good”，写 JSON 报告并按退出码返回。
    /// scripts/verify.ps1 就是靠这个在无人值守下判断 10 个 UI 是否真的跑起来了。
    /// </summary>
    private void StartSmokeTimer(int seconds)
    {
        _smokeTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1000, seconds * 1000) };
        _smokeTimer.Tick += async (_, _) =>
        {
            _smokeTimer.Stop();
            var stats = _engine.Stats;
            var quality = _store.Get("level").Quality;
            await StopAsync();

            var success = stats.Cycles >= 2 && stats.Successes >= 2 && quality == TagQuality.Good;
            var report = SmokeReport.Write("Scada.WinForms.V1.SeeData", success, new Dictionary<string, object?>
            {
                ["cycles"] = stats.Cycles,
                ["successes"] = stats.Successes,
                ["failures"] = stats.Failures,
                ["level_quality"] = quality.ToString(),
                ["level_value"] = _store.Get("level").Value,
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
        _ = DisposeEngineAsync();
        base.OnFormClosed(e);
    }

    private async Task DisposeEngineAsync()
    {
        try { await _engine.DisposeAsync(); }
        catch { /* 退出阶段不抛 */ }
    }
}
