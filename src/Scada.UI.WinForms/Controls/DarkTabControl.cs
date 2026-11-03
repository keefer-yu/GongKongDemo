using System.Drawing;
using System.Windows.Forms;
using Scada.UI.WinForms.Theme;

namespace Scada.UI.WinForms.Controls;

/// <summary>WinForms 原生 TabControl 不容易做深色，这里用 OwnerDraw 统一风格。</summary>
public sealed class DarkTabControl : TabControl
{
    public DarkTabControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(110, 32);
        Font = IndustrialTheme.FontSmall;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var selected = e.Index == SelectedIndex;
        var bounds = GetTabRect(e.Index);
        using var back = new SolidBrush(selected ? IndustrialTheme.PanelAlt : IndustrialTheme.Panel);
        e.Graphics.FillRectangle(back, bounds);
        using var border = new Pen(selected ? IndustrialTheme.Accent : IndustrialTheme.Border);
        e.Graphics.DrawRectangle(border, bounds);

        var text = TabPages[e.Index].Text;
        TextRenderer.DrawText(e.Graphics, text, Font, bounds,
            selected ? IndustrialTheme.Accent : IndustrialTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
