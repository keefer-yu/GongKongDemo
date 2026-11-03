using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Scada.Core.Tags;

namespace Scada.UI.Wpf.Converters;

public sealed class QualityToBrushConverter : IValueConverter
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x4E, 0xD6, 0x8E));
    private static readonly Brush Uncertain = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x4C));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFF, 0x56, 0x60));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TagQuality quality
            ? quality switch
            {
                TagQuality.Good => Good,
                TagQuality.Uncertain => Uncertain,
                _ => Bad,
            }
            : Bad;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
