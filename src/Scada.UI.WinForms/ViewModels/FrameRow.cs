namespace Scada.UI.WinForms.ViewModels;

public sealed class FrameRow
{
    public required string Time { get; init; }
    public required string Direction { get; init; }
    public required string Hex { get; init; }
    public required string Summary { get; init; }
}
