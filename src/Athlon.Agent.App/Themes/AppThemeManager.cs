using System.Windows;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Themes;

/// <summary>Applies theme palettes to WPF resources and exposes colors for code-behind controls.</summary>
public static class AppThemeManager
{
    public static AppThemePalette Current { get; private set; } = DarkAppThemePalette.Create();

    public static event EventHandler? ThemeChanged;

    public static AppThemeKind CurrentKind => Current.Kind;

    public static void Apply(AppThemeKind kind)
    {
        Current = kind switch
        {
            AppThemeKind.Light => LightAppThemePalette.Create(),
            _ => DarkAppThemePalette.Create(),
        };

        if (Application.Current?.Resources is ResourceDictionary root)
        {
            AppThemeResourceBuilder.ApplyPalette(root, Current.Chrome);
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyFromSettings(UiSettings ui) => Apply(ParseKind(ui.Theme));

    public static AppThemeKind ParseKind(string? themeName) =>
        themeName?.Trim().Equals("Light", StringComparison.OrdinalIgnoreCase) == true
            ? AppThemeKind.Light
            : AppThemeKind.Dark;

    /// <summary>Switch theme and optionally persist to settings (for future UI toggle).</summary>
    public static void SetTheme(AppThemeKind kind, UiSettings? uiSettings = null)
    {
        if (uiSettings is not null)
        {
            uiSettings.Theme = kind == AppThemeKind.Light ? "Light" : "Dark";
        }

        Apply(kind);
    }

    public static AppThemePalette GetPalette(AppThemeKind kind) =>
        kind switch
        {
            AppThemeKind.Light => LightAppThemePalette.Create(),
            _ => DarkAppThemePalette.Create(),
        };
}
