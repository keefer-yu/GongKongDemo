using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Scada.Core.Tags;

namespace Scada.UI.Wpf.Converters;

public sealed class AlarmLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AlarmLevel level
            ? level switch
            {
                AlarmLevel.HH or AlarmLevel.LL => new SolidColorBrush(Color.FromRgb(0xFF, 0x56, 0x60)),
                AlarmLevel.H or AlarmLevel.L => new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x4C)),
                _ => Brushes.Transparent,
            }
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
