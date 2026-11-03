using System.Drawing;
using System.Windows.Forms;

namespace Scada.UI.WinForms.Theme;

/// <summary>深色工业风配色。所有 Demo 共用，保证 5 个版本看起来是同一个产品。</summary>
public static class IndustrialTheme
{
    public static readonly Color Background = Color.FromArgb(15, 23, 34);
    public static readonly Color Panel = Color.FromArgb(24, 35, 50);
    public static readonly Color PanelAlt = Color.FromArgb(30, 44, 62);
    public static readonly Color Border = Color.FromArgb(52, 72, 92);
    public static readonly Color Text = Color.FromArgb(226, 236, 246);
    public static readonly Color Muted = Color.FromArgb(142, 160, 178);
    public static readonly Color Accent = Color.FromArgb(0, 200, 180);
    public static readonly Color AccentDark = Color.FromArgb(0, 120, 110);
    public static readonly Color Success = Color.FromArgb(78, 214, 142);
    public static readonly Color Warning = Color.FromArgb(255, 184, 76);
    public static readonly Color Danger = Color.FromArgb(255, 86, 96);
    public static readonly Color Info = Color.FromArgb(90, 170, 255);

    public static readonly Font FontSmall = new("Microsoft YaHei UI", 9f);
    public static readonly Font FontNormal = new("Microsoft YaHei UI", 10f);
    public static readonly Font FontTitle = new("Microsoft YaHei UI", 15f, FontStyle.Bold);
    public static readonly Font FontNumber = new("Consolas", 20f, FontStyle.Bold);
    public static readonly Font FontMono = new("Consolas", 9.5f);

    public static void StyleForm(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = FontNormal;
    }

    public static void StylePanel(Control control, bool alt = false)
    {
        control.BackColor = alt ? PanelAlt : Panel;
        control.ForeColor = Text;
    }

    public static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.BackColor = primary ? AccentDark : PanelAlt;
        button.ForeColor = primary ? Color.White : Text;
        button.Font = FontSmall;
        button.Cursor = Cursors.Hand;
        button.Padding = new Padding(8, 4, 8, 4);
        button.UseVisualStyleBackColor = false;
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Panel;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = PanelAlt;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Text;
        grid.ColumnHeadersDefaultCellStyle.Font = FontSmall;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = PanelAlt;
        grid.DefaultCellStyle.BackColor = Panel;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(42, 70, 88);
        grid.DefaultCellStyle.SelectionForeColor = Text;
        grid.DefaultCellStyle.Font = FontSmall;
        grid.RowHeadersDefaultCellStyle.BackColor = PanelAlt;
        grid.RowHeadersDefaultCellStyle.ForeColor = Muted;
        grid.GridColor = Border;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
    }
}
