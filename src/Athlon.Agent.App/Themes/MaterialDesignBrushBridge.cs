using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

/// <summary>
/// Supplies the <c>MaterialDesign.Brush.*</c> keys that MaterialDesign's control styles resolve at
/// runtime.
///
/// The app loads <c>MaterialDesign3.Defaults.xaml</c> for MaterialDesign's control styles but never
/// installs a MaterialDesign theme: there is no <c>BundledTheme</c>, no <c>PaletteHelper</c> and no
/// <c>ResourceDictionaryExtensions.SetTheme</c> call anywhere in the app. Those ~100 keys are
/// written <em>only</em> by <c>SetTheme</c>, so without this bridge every one of them is missing from
/// the resource tree. (The Defaults dictionary itself defines none of them; it only merges the
/// control styles.)
///
/// A missing <c>DynamicResource</c> resolves to an unset brush, which paints nothing. That is why
/// MaterialDesign controls the app does not re-template render see-through — most visibly the
/// DatePicker/Calendar popup, whose surface is <c>MaterialDesign.Brush.Background</c> and which
/// therefore appeared transparent over the window behind it. Controls the app styles itself
/// (<c>ComboBox</c>, <c>ToolTip</c>, <c>ContextMenu</c>) were unaffected because their templates
/// reference the app's own <c>Brush.*</c> palette instead.
///
/// Keys are derived from the app palette so MaterialDesign controls match the shell, and every
/// surface key maps to an opaque color — a translucent value would reintroduce the bug.
/// </summary>
internal static class MaterialDesignBrushBridge
{
    /// <summary>
    /// Surface behind MaterialDesign popups and cards. The DatePicker/Calendar flyout paints its
    /// background from this key; it is the one whose absence made the calendar transparent.
    /// </summary>
    internal const string SurfaceKey = "MaterialDesign.Brush.Background";

    internal const string ForegroundKey = "MaterialDesign.Brush.Foreground";
    internal const string PrimaryKey = "MaterialDesign.Brush.Primary";
    internal const string PrimaryForegroundKey = "MaterialDesign.Brush.Primary.Foreground";

    /// <summary>Builds the MaterialDesign brush keys for one app palette.</summary>
    internal static IReadOnlyDictionary<string, object> Build(UiChromeColors c)
    {
        var onAccent = B(Colors.White);
        var resources = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            // Core surface + text. MaterialDesign uses these as the default for any control it
            // templates itself, so they must stay opaque.
            [SurfaceKey] = B(c.MenuBackground),
            [ForegroundKey] = B(c.Text),
            ["MaterialDesign.Brush.ForegroundLight"] = B(c.SubtleText),
            ["MaterialDesign.Brush.ValidationError"] = B(c.Danger),

            // Primary / secondary accents. The app has a single accent, so both families derive
            // from it and stay monochromatic with the shell.
            ["MaterialDesign.Brush.Primary.Light"] = B(c.Accent),
            [PrimaryKey] = B(c.Accent),
            ["MaterialDesign.Brush.Primary.Dark"] = B(c.AccentActive),
            ["MaterialDesign.Brush.Primary.Light.Foreground"] = onAccent,
            [PrimaryForegroundKey] = onAccent,
            ["MaterialDesign.Brush.Primary.Dark.Foreground"] = onAccent,
            ["MaterialDesign.Brush.Secondary.Light"] = B(c.Accent),
            ["MaterialDesign.Brush.Secondary"] = B(c.Accent),
            ["MaterialDesign.Brush.Secondary.Dark"] = B(c.AccentActive),
            ["MaterialDesign.Brush.Secondary.Light.Foreground"] = onAccent,
            ["MaterialDesign.Brush.Secondary.Foreground"] = onAccent,
            ["MaterialDesign.Brush.Secondary.Dark.Foreground"] = onAccent,

            // Cards / chips / badges / color zones
            ["MaterialDesign.Brush.Card.Background"] = B(c.Panel),
            ["MaterialDesign.Brush.Card.Border"] = B(c.Border),
            ["MaterialDesign.Brush.Chip.Background"] = B(c.PanelAlt),
            ["MaterialDesign.Brush.Chip.OutlineBorder"] = B(c.Border),
            ["MaterialDesign.Brush.Badged.DarkBackground"] = B(c.PanelAlt),
            ["MaterialDesign.Brush.Badged.DarkForeground"] = B(c.Text),
            ["MaterialDesign.Brush.Badged.LightBackground"] = B(c.Accent),
            ["MaterialDesign.Brush.Badged.LightForeground"] = onAccent,
            ["MaterialDesign.Brush.ColorZone.DarkBackground"] = B(c.PanelAlt),
            ["MaterialDesign.Brush.ColorZone.DarkForeground"] = B(c.Text),
            ["MaterialDesign.Brush.ColorZone.LightBackground"] = B(c.Panel),
            ["MaterialDesign.Brush.ColorZone.LightForeground"] = B(c.Text),

            // Buttons (ripple / pressed overlays)
            ["MaterialDesign.Brush.Button.FlatClick"] = B(c.HoverSurfacePressed),
            ["MaterialDesign.Brush.Button.Ripple"] = B(c.HoverSurface),
            ["MaterialDesign.Brush.Button.FlatRipple"] = B(c.HoverSurface),

            // Selection controls
            ["MaterialDesign.Brush.CheckBox.Disabled"] = B(c.DisabledText),
            ["MaterialDesign.Brush.CheckBox.Off"] = B(c.DisabledText),
            ["MaterialDesign.Brush.CheckBox.UncheckedBorder"] = B(c.Border),
            ["MaterialDesign.Brush.RadioButton.Border"] = B(c.Border),
            ["MaterialDesign.Brush.RadioButton.Checked"] = B(c.Accent),
            ["MaterialDesign.Brush.RadioButton.Disabled"] = B(c.DisabledText),
            ["MaterialDesign.Brush.RadioButton.Outline"] = B(c.Border),
            ["MaterialDesign.Brush.RadioButton.Chip.CheckedBackground"] = B(c.AccentSubtle),
            ["MaterialDesign.Brush.ToggleButton.Background"] = B(c.SurfaceHover),
            ["MaterialDesign.Brush.ToggleButton.Foreground"] = B(c.Text),
            ["MaterialDesign.Brush.ToggleButton.Switch.TrackOffBackground"] = B(c.DisabledBackground),

            // Text inputs
            ["MaterialDesign.Brush.TextBox.Border"] = B(c.Border),
            ["MaterialDesign.Brush.TextBox.ComboBoxHover"] = B(c.HoverSurface),
            ["MaterialDesign.Brush.TextBox.DisabledBackground"] = B(c.DisabledBackground),
            ["MaterialDesign.Brush.TextBox.FilledBackground"] = B(c.Chrome),
            ["MaterialDesign.Brush.TextBox.HoverBackground"] = B(c.HoverSurface),
            ["MaterialDesign.Brush.TextBox.HoverBorder"] = B(c.BorderHover),
            ["MaterialDesign.Brush.TextBox.OutlineBorder"] = B(c.Border),
            ["MaterialDesign.Brush.TextBox.OutlineInactiveBorder"] = B(c.Border),
            ["MaterialDesign.Brush.PasswordBox.Border"] = B(c.Border),
            ["MaterialDesign.Brush.PasswordBox.FilledBackground"] = B(c.Chrome),
            ["MaterialDesign.Brush.PasswordBox.HoverBackground"] = B(c.HoverSurface),
            ["MaterialDesign.Brush.PasswordBox.HoverBorder"] = B(c.BorderHover),
            ["MaterialDesign.Brush.PasswordBox.OutlineBorder"] = B(c.Border),
            ["MaterialDesign.Brush.PasswordBox.OutlineInactiveBorder"] = B(c.Border),

            // ComboBox (the app re-templates ComboBox; these keep the leftovers consistent)
            ["MaterialDesign.Brush.ComboBox.Border"] = B(c.Border),
            ["MaterialDesign.Brush.ComboBox.Disabled"] = B(c.DisabledText),
            ["MaterialDesign.Brush.ComboBox.FilledBackground"] = B(c.Chrome),
            ["MaterialDesign.Brush.ComboBox.HoverBackground"] = B(c.HoverSurface),
            ["MaterialDesign.Brush.ComboBox.HoverBorder"] = B(c.BorderHover),
            ["MaterialDesign.Brush.ComboBox.OutlineBorder"] = B(c.Border),
            ["MaterialDesign.Brush.ComboBox.OutlineInactiveBorder"] = B(c.Border),
            ["MaterialDesign.Brush.ComboBox.Popup.DarkBackground"] = B(c.MenuBackground),
            ["MaterialDesign.Brush.ComboBox.Popup.DarkForeground"] = B(c.Text),
            ["MaterialDesign.Brush.ComboBox.Popup.LightBackground"] = B(c.MenuBackground),
            ["MaterialDesign.Brush.ComboBox.Popup.LightForeground"] = B(c.Text),

            // Lists
            ["MaterialDesign.Brush.ListBoxItem.Border"] = B(c.Border),
            ["MaterialDesign.Brush.ListBoxItem.Selected"] = B(c.SelectionActive),
            ["MaterialDesign.Brush.ListView.Hover"] = B(c.HoverNeutral),
            ["MaterialDesign.Brush.ListView.Selected"] = B(c.SelectionActive),
            ["MaterialDesign.Brush.ListView.Separator"] = B(c.Border),

            // Data grid
            ["MaterialDesign.Brush.DataGrid.Border"] = B(c.Border),
            ["MaterialDesign.Brush.DataGrid.ButtonPressed"] = B(c.HoverSurfacePressed),
            ["MaterialDesign.Brush.DataGrid.ColumnHeaderForeground"] = B(c.TextSecondary),
            ["MaterialDesign.Brush.DataGrid.ComboBoxHover"] = B(c.HoverSurface),
            ["MaterialDesign.Brush.DataGrid.ComboBoxSelected"] = B(c.SelectionActive),
            ["MaterialDesign.Brush.DataGrid.PopupBorder"] = B(c.Border),
            ["MaterialDesign.Brush.DataGrid.RowHoverBackground"] = B(c.HoverNeutral),
            ["MaterialDesign.Brush.DataGrid.Selected"] = B(c.SelectionActive),

            // Scroll / splitter / separator / tabs / header
            ["MaterialDesign.Brush.ScrollBar.ActiveBackground"] = B(c.HoverSurface),
            ["MaterialDesign.Brush.ScrollBar.Foreground"] = B(c.ScrollThumb),
            ["MaterialDesign.Brush.ScrollBar.RepeatButtonBackground"] = B(c.SurfaceHover),
            ["MaterialDesign.Brush.GridSplitter.Background"] = B(c.Border),
            ["MaterialDesign.Brush.GridSplitter.PreviewBackground"] = B(c.HoverSurface),
            ["MaterialDesign.Brush.Separator.Background"] = B(c.Border),
            ["MaterialDesign.Brush.TabControl.Divider"] = B(c.Border),
            ["MaterialDesign.Brush.Header.Foreground"] = B(c.TextSecondary),

            // Bars, toolbars and transient surfaces
            ["MaterialDesign.Brush.StatusBar.Background"] = B(c.Chrome),
            ["MaterialDesign.Brush.StatusBar.Foreground"] = B(c.Text),
            ["MaterialDesign.Brush.ToolBar.Background"] = B(c.Chrome),
            ["MaterialDesign.Brush.ToolBar.Item.Background"] = B(c.Chrome),
            ["MaterialDesign.Brush.ToolBar.Item.Foreground"] = B(c.Text),
            ["MaterialDesign.Brush.ToolBar.Overflow.Border"] = B(c.Border),
            ["MaterialDesign.Brush.ToolBar.Separator"] = B(c.Border),
            ["MaterialDesign.Brush.ToolBar.Thumb.Foreground"] = B(c.SubtleText),
            ["MaterialDesign.Brush.ToolTip.Background"] = B(c.MenuBackground),
            ["MaterialDesign.Brush.SnackBar.Background"] = B(c.ToastBackground),
            ["MaterialDesign.Brush.SnackBar.MouseOver"] = B(c.MenuHover),
            ["MaterialDesign.Brush.SnackBar.Ripple"] = B(c.HoverSurface),
        };

        return resources;
    }

    private static SolidColorBrush B(Color color) => AppThemeColor.ToFrozenBrush(color);
}
