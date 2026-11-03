namespace Scada.UI.Wpf.ViewModels;

public sealed class FrameRowViewModel
{
    public required string Time { get; init; }
    public required string Direction { get; init; }
    public required string Hex { get; init; }
    public required string Summary { get; init; }
}
