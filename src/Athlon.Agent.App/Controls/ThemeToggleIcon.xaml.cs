using System.Windows;
using System.Windows.Controls;

namespace Athlon.Agent.App.Controls;

public partial class ThemeToggleIcon : UserControl
{
    public static readonly DependencyProperty IsLightThemeProperty =
        DependencyProperty.Register(
            nameof(IsLightTheme),
            typeof(bool),
            typeof(ThemeToggleIcon),
            new PropertyMetadata(false, OnIsLightThemeChanged));

    public ThemeToggleIcon()
    {
        InitializeComponent();
        ApplyThemeGlyph(IsLightTheme);
    }

    public bool IsLightTheme
    {
        get => (bool)GetValue(IsLightThemeProperty);
        set => SetValue(IsLightThemeProperty, value);
    }

    private static void OnIsLightThemeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThemeToggleIcon icon && e.NewValue is bool isLight)
        {
            icon.ApplyThemeGlyph(isLight);
        }
    }

    private void ApplyThemeGlyph(bool isLight)
    {
        // Light theme → show moon (switch to dark); dark theme → show sun.
        MoonIcon.Visibility = isLight ? Visibility.Visible : Visibility.Collapsed;
        SunIcon.Visibility = isLight ? Visibility.Collapsed : Visibility.Visible;
    }
}
