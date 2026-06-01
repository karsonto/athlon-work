using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

public static class AppThemeColor
{
    public static Color FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromRgb(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    public static SolidColorBrush ToFrozenBrush(Color color, double? opacity = null)
    {
        var brush = opacity is null
            ? new SolidColorBrush(color)
            : new SolidColorBrush(color) { Opacity = opacity.Value };
        brush.Freeze();
        return brush;
    }
}
