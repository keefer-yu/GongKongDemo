using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Scada.UI.WinForms.Theme;

namespace Scada.UI.WinForms.Controls;

public sealed class TrendSeries
{
    public required string Name { get; init; }
    public required Color Color { get; init; }
    public List<double> Values { get; } = [];
}

/// <summary>无第三方依赖的实时趋势图：固定窗口、自动网格、最多保留 600 点。</summary>
public sealed class TrendChart : Control
{
    private readonly List<TrendSeries> _series = [];
    private readonly object _gate = new();
    private double _min;
    private double _max = 100;
    private int _capacity = 600;

    public TrendChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = IndustrialTheme.Panel;
        ForeColor = IndustrialTheme.Text;
        Font = IndustrialTheme.FontSmall;
        Padding = new Padding(8);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double Min { get => _min; set { _min = value; Invalidate(); } }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double Max { get => _max; set { _max = Math.Max(value, _min + 0.001); Invalidate(); } }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Capacity { get => _capacity; set => _capacity = Math.Max(10, value); }

    public void SetRange(double min, double max)
    {
        _min = min;
        _max = Math.Max(max, min + 0.001);
        Invalidate();
    }

    public void AddSeries(string name, Color color)
    {
        lock (_gate)
        {
            if (_series.All(s => s.Name != name))
                _series.Add(new TrendSeries { Name = name, Color = color });
        }
        Invalidate();
    }

    public void AddPoint(string name, double value)
    {
        lock (_gate)
        {
            var series = _series.FirstOrDefault(s => s.Name == name);
            if (series is null) return;
            series.Values.Add(value);
            if (series.Values.Count > _capacity)
                series.Values.RemoveAt(0);
        }
        Invalidate();
    }

    public void Clear()
    {
        lock (_gate)
            foreach (var series in _series) series.Values.Clear();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var plot = new Rectangle(44, 12, Math.Max(10, Width - 56), Math.Max(10, Height - 40));
        using var gridPen = new Pen(Color.FromArgb(45, 70, 90), 1);
        using var axisPen = new Pen(IndustrialTheme.Border, 1);
        using var textBrush = new SolidBrush(IndustrialTheme.Muted);

        for (var i = 0; i <= 5; i++)
        {
            var y = plot.Top + plot.Height * i / 5;
            e.Graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            var value = _max - (_max - _min) * i / 5;
            e.Graphics.DrawString(value.ToString("F1"), Font, textBrush, 2, y - 7);
        }

        e.Graphics.DrawRectangle(axisPen, plot);

        lock (_gate)
        {
            if (_series.Count == 0 || _series.All(s => s.Values.Count == 0))
            {
                e.Graphics.DrawString("等待数据…", IndustrialTheme.FontNormal, textBrush,
                    plot.Left + 12, plot.Top + 12);
                return;
            }

            foreach (var series in _series)
            {
                if (series.Values.Count < 2) continue;
                var points = new PointF[series.Values.Count];
                var count = series.Values.Count;
                for (var i = 0; i < count; i++)
                {
                    var x = plot.Left + (float)plot.Width * i / Math.Max(1, _capacity - 1);
                    var ratio = Math.Clamp((series.Values[i] - _min) / (_max - _min), 0, 1);
                    var y = plot.Bottom - (float)plot.Height * (float)ratio;
                    points[i] = new PointF(x, y);
                }

                using var pen = new Pen(series.Color, 1.8f);
                e.Graphics.DrawLines(pen, points);
            }

            var legendX = plot.Left + 4;
            foreach (var series in _series)
            {
                using var brush = new SolidBrush(series.Color);
                e.Graphics.FillRectangle(brush, legendX, plot.Bottom + 10, 12, 4);
                e.Graphics.DrawString(series.Name, Font, textBrush, legendX + 16, plot.Bottom + 4);
                legendX += 24 + (int)e.Graphics.MeasureString(series.Name, Font).Width;
            }
        }
    }
}
