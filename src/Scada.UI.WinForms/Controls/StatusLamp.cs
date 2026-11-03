using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Scada.Core.Tags;
using Scada.UI.WinForms.Theme;

namespace Scada.UI.WinForms.Controls;

public sealed class StatusLamp : Control
{
    private bool _isOn;
    private bool _isFault;

    public StatusLamp()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(18, 18);
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsOn
    {
        get => _isOn;
        set { if (_isOn == value) return; _isOn = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsFault
    {
        get => _isFault;
        set { if (_isFault == value) return; _isFault = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var color = _isFault ? IndustrialTheme.Danger : _isOn ? IndustrialTheme.Success : IndustrialTheme.Muted;
        var center = new Point(Width / 2, Height / 2);
        var radius = Math.Max(3, Math.Min(Width, Height) / 2 - 3);
        var glow = new Rectangle(center.X - radius - 3, center.Y - radius - 3, radius * 2 + 6, radius * 2 + 6);
        using var glowBrush = new SolidBrush(Color.FromArgb(_isOn ? 55 : 20, color));
        e.Graphics.FillEllipse(glowBrush, glow);

        var body = new Rectangle(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        using var brush = new SolidBrush(color);
        e.Graphics.FillEllipse(brush, body);
        using var pen = new Pen(Color.FromArgb(180, Color.Black), 1);
        e.Graphics.DrawEllipse(pen, body);
    }
}
