using System.Windows.Forms;

namespace Scada.UI.WinForms.Controls;

/// <summary>显式列定义，避免 DataGridView 自动生成列导致表头/顺序不可控。</summary>
public static class GridColumns
{
    public static void ApplyTagColumns(DataGridView grid)
    {
        grid.AutoGenerateColumns = false;
        grid.Columns.Clear();
        grid.Columns.Add(Text(nameof(ViewModels.TagRow.Name), "点位", 90));
        grid.Columns.Add(Text(nameof(ViewModels.TagRow.Address), "地址", 70));
        grid.Columns.Add(Text(nameof(ViewModels.TagRow.Value), "工程值", 100));
        grid.Columns.Add(Text(nameof(ViewModels.TagRow.Unit), "单位", 50));
        grid.Columns.Add(Text(nameof(ViewModels.TagRow.Quality), "质量", 60));
        grid.Columns.Add(Text(nameof(ViewModels.TagRow.RawValue), "原始值", 80));
        grid.Columns.Add(Text(nameof(ViewModels.TagRow.UpdatedAt), "更新时间", 100));
    }

    public static void ApplyFrameColumns(DataGridView grid)
    {
        grid.AutoGenerateColumns = false;
        grid.Columns.Clear();
        grid.Columns.Add(Text(nameof(ViewModels.FrameRow.Time), "时间", 96));
        grid.Columns.Add(Text(nameof(ViewModels.FrameRow.Direction), "方向", 50));
        grid.Columns.Add(Text(nameof(ViewModels.FrameRow.Hex), "十六进制", 280));
        grid.Columns.Add(Text(nameof(ViewModels.FrameRow.Summary), "解码", 280));
    }

    private static DataGridViewTextBoxColumn Text(string property, string header, int width) => new()
    {
        DataPropertyName = property,
        HeaderText = header,
        Width = width,
        FillWeight = width,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };
}
