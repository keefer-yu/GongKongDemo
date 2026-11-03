using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Scada.UI.WinForms.Theme;

namespace Scada.UI.WinForms.Controls;

public sealed class ValueBar : Control
{
    private double _value;
    private double _min;
    private double _max = 100;

    public ValueBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        Height = 8;
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Min { get => _min; set { _min = value; Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Max { get => _max; set { _max = Math.Max(value, _min + 0.0001); Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set
        {
            if (Math.Abs(_value - value) < 0.0001) return;
            _value = value;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BarColor { get; set; } = IndustrialTheme.Accent;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var back = new SolidBrush(IndustrialTheme.PanelAlt);
        using var border = new Pen(IndustrialTheme.Border);
        e.Graphics.FillRectangle(back, rect);
        e.Graphics.DrawRectangle(border, rect);

        var ratio = Math.Clamp((_value - _min) / (_max - _min), 0, 1);
        var fillWidth = (int)Math.Round((rect.Width - 2) * ratio);
        if (fillWidth > 0)
        {
            var fill = new Rectangle(1, 1, fillWidth, rect.Height - 2);
            using var brush = new SolidBrush(BarColor);
            e.Graphics.FillRectangle(brush, fill);
        }
    }
}
