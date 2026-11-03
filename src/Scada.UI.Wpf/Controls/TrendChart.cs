using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Scada.UI.Wpf.Controls;

public sealed class TrendSeries
{
    public required string Name { get; init; }
    public required Brush Brush { get; init; }
    public List<double> Values { get; } = [];
}

/// <summary>WPF 版实时趋势图；用 DrawingContext 绘制，不依赖图表库。</summary>
public sealed class TrendChart : FrameworkElement
{
    private readonly List<TrendSeries> _series = [];
    private readonly object _gate = new();
    private double _min;
    private double _max = 100;
    private int _capacity = 600;

    public TrendChart()
    {
        SnapsToDevicePixels = true;
    }

    public double Min { get => _min; set { _min = value; InvalidateVisual(); } }
    public double Max { get => _max; set { _max = Math.Max(value, _min + 0.001); InvalidateVisual(); } }
    public int Capacity { get => _capacity; set => _capacity = Math.Max(10, value); }

    public void SetRange(double min, double max)
    {
        _min = min;
        _max = Math.Max(max, min + 0.001);
        InvalidateVisual();
    }

    public void AddSeries(string name, Brush brush)
    {
        lock (_gate)
        {
            if (_series.All(s => s.Name != name))
                _series.Add(new TrendSeries { Name = name, Brush = brush });
        }
        InvalidateVisual();
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
        InvalidateVisual();
    }

    public void Clear()
    {
        lock (_gate)
            foreach (var series in _series) series.Values.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var background = new SolidColorBrush(Color.FromRgb(0x18, 0x23, 0x32));
        dc.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var plot = new Rect(48, 12, Math.Max(20, ActualWidth - 64), Math.Max(20, ActualHeight - 46));
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x2D, 0x45, 0x5A)), 1);
        var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(0x34, 0x48, 0x5C)), 1);
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x8E, 0xA0, 0xB2));

        for (var i = 0; i <= 5; i++)
        {
            var y = plot.Top + plot.Height * i / 5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var value = _max - (_max - _min) * i / 5;
            dc.DrawText(Text(value.ToString("F1"), labelBrush, 10), new Point(4, y - 8));
        }

        dc.DrawRectangle(null, borderPen, plot);

        lock (_gate)
        {
            if (_series.Count == 0 || _series.All(s => s.Values.Count == 0))
            {
                dc.DrawText(Text("等待数据…", labelBrush, 13), new Point(plot.Left + 12, plot.Top + 12));
                return;
            }

            foreach (var series in _series)
            {
                if (series.Values.Count < 2) continue;
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    for (var i = 0; i < series.Values.Count; i++)
                    {
                        var x = plot.Left + plot.Width * i / Math.Max(1, _capacity - 1);
                        var ratio = Math.Clamp((series.Values[i] - _min) / (_max - _min), 0, 1);
                        var y = plot.Bottom - plot.Height * ratio;
                        if (i == 0) context.BeginFigure(new Point(x, y), false, false);
                        else context.LineTo(new Point(x, y), true, false);
                    }
                }
                geometry.Freeze();
                dc.DrawGeometry(null, new Pen(series.Brush, 1.8), geometry);
            }

            var legendX = plot.Left + 4;
            foreach (var series in _series)
            {
                dc.DrawRectangle(series.Brush, null, new Rect(legendX, plot.Bottom + 12, 12, 4));
                var label = Text(series.Name, labelBrush, 11);
                dc.DrawText(label, new Point(legendX + 16, plot.Bottom + 4));
                legendX += 24 + label.Width;
            }
        }
    }

    private FormattedText Text(string text, Brush brush, double size)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
