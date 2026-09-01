using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Athlon.Agent.App.Services;

namespace Athlon.Agent.App.Themes;

public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
        {
            return Brushes.Transparent;
        }

        var brush = ThemeBrushResolver.Get(key);
        if (parameter is string mode
            && string.Equals(mode, "subtle", StringComparison.OrdinalIgnoreCase)
            && brush is SolidColorBrush solid)
        {
            var color = solid.Color;
            return new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        }

        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
