using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace Athlon.Agent.App.Themes;

internal static class AppPackIconHelper
{
    public static PackIcon Create(
        PackIconKind kind,
        double size,
        Brush? foreground = null,
        Thickness? margin = null,
        double opacity = 1)
    {
        return new PackIcon
        {
            Kind = kind,
            Width = size,
            Height = size,
            Opacity = opacity,
            Margin = margin ?? new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = foreground
        };
    }
}
