using System.Drawing;
using System.Windows.Forms;
using Scada.Core.Tags;
using Scada.UI.WinForms.Theme;

namespace Scada.UI.WinForms.Controls;

/// <summary>单个点位卡：名称、工程值、单位、质量码、模拟量进度条或开关状态灯。</summary>
public sealed class TagCard : UserControl
{
    private readonly TagDefinition _tag;
    private readonly Label _nameLabel;
    private readonly Label _qualityLabel;
    private readonly Label _valueLabel;
    private readonly Label _unitLabel;
    private readonly ValueBar _bar;
    private readonly StatusLamp _lamp;

    public TagCard(TagDefinition tag)
    {
        _tag = tag;
        BackColor = IndustrialTheme.Panel;
        ForeColor = IndustrialTheme.Text;
        Padding = new Padding(12, 10, 12, 10);
        Margin = new Padding(5);
        Height = 100;
        DoubleBuffered = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _nameLabel = new Label
        {
            Text = tag.Name,
            Dock = DockStyle.Left,
            AutoSize = true,
            Font = IndustrialTheme.FontNormal,
            ForeColor = IndustrialTheme.Text,
        };
        _qualityLabel = new Label
        {
            Text = "未采集",
            Dock = DockStyle.Right,
            AutoSize = true,
            Font = IndustrialTheme.FontSmall,
            ForeColor = IndustrialTheme.Muted,
        };
        header.Controls.Add(_nameLabel);
        header.Controls.Add(_qualityLabel);

        _valueLabel = new Label
        {
            Text = "--",
            Dock = DockStyle.Fill,
            Font = IndustrialTheme.FontNumber,
            ForeColor = IndustrialTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };

        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _unitLabel = new Label
        {
            Text = tag.Unit,
            Dock = DockStyle.Left,
            AutoSize = true,
            Font = IndustrialTheme.FontSmall,
            ForeColor = IndustrialTheme.Muted,
        };
        _bar = new ValueBar
        {
            Dock = DockStyle.Fill,
            Min = 0,
            Max = DefaultMax(tag),
            Visible = !tag.IsBool,
        };
        _lamp = new StatusLamp
        {
            Dock = DockStyle.Right,
            Visible = tag.IsBool,
            Margin = new Padding(4, 0, 4, 0),
        };
        footer.Controls.Add(_bar);
        footer.Controls.Add(_lamp);
        footer.Controls.Add(_unitLabel);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_valueLabel, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
    }

    public TagDefinition TagDefinition => _tag;

    public void UpdateValue(TagValue value)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<TagValue>(UpdateValue), value);
            return;
        }

        if (_tag.IsBool)
        {
            _lamp.IsOn = value.IsGood && value.Value != 0;
            _lamp.IsFault = !value.IsGood;
            _valueLabel.Text = value.IsGood ? (value.Value != 0 ? "ON" : "OFF") : "ERR";
        }
        else
        {
            _valueLabel.Text = value.IsGood ? value.Value.ToString($"F{_tag.Decimals}") : "ERR";
            _bar.Value = value.IsGood ? value.Value : 0;
        }

        _qualityLabel.Text = value.Quality switch
        {
            TagQuality.Good => "良好",
            TagQuality.Uncertain => "不确定",
            TagQuality.Stale => "陈旧",
            _ => "坏值",
        };
        _qualityLabel.ForeColor = value.Quality switch
        {
            TagQuality.Good => IndustrialTheme.Success,
            TagQuality.Uncertain => IndustrialTheme.Warning,
            _ => IndustrialTheme.Danger,
        };
        _valueLabel.ForeColor = value.IsGood ? IndustrialTheme.Text : IndustrialTheme.Muted;
        _bar.BarColor = AlarmColor(value.Value);
    }

    private Color AlarmColor(double value)
    {
        var alarm = _tag.Alarm;
        if (alarm is null) return IndustrialTheme.Accent;
        if (alarm.HH is { } hh && value >= hh) return IndustrialTheme.Danger;
        if (alarm.H is { } h && value >= h) return IndustrialTheme.Warning;
        if (alarm.LL is { } ll && value <= ll) return IndustrialTheme.Danger;
        if (alarm.L is { } l && value <= l) return IndustrialTheme.Info;
        return IndustrialTheme.Accent;
    }

    private static double DefaultMax(TagDefinition tag) => tag.Id.ToLowerInvariant() switch
    {
        "level" => 100,
        "temp" => 150,
        "pressure" => 1,
        "tempsetpoint" => 120,
        _ => 100,
    };
}
