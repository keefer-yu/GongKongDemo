using System.Windows;
using System.Windows.Media;

namespace Scada.UI.Wpf.Controls;

public sealed class StatusLamp : FrameworkElement
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(StatusLamp),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsFaultProperty = DependencyProperty.Register(
        nameof(IsFault), typeof(bool), typeof(StatusLamp),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(StatusLamp),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure |
                                              FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public bool IsFault
    {
        get => (bool)GetValue(IsFaultProperty);
        set => SetValue(IsFaultProperty, value);
    }

    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(Diameter, Diameter);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var color = IsFault
            ? Color.FromRgb(0xFF, 0x56, 0x60)
            : IsOn ? Color.FromRgb(0x4E, 0xD6, 0x8E) : Color.FromRgb(0x8E, 0xA0, 0xB2);
        var center = new Point(Diameter / 2, Diameter / 2);
        var radius = Math.Max(2, Diameter / 2 - 2);
        drawingContext.DrawEllipse(new SolidColorBrush(Color.FromArgb(IsOn ? (byte)60 : (byte)20, color.R, color.G, color.B)),
            null, center, radius + 2, radius + 2);
        drawingContext.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.Black, 0.6), center, radius, radius);
    }
}
