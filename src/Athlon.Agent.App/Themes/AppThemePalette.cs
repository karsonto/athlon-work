namespace Athlon.Agent.App.Themes;

/// <summary>Complete color palette for one application theme.</summary>
public sealed class AppThemePalette
{
    public required AppThemeKind Kind { get; init; }
    public required UiChromeColors Chrome { get; init; }
    public required EditorThemeColors Editor { get; init; }
    public required WorkspaceFileIconThemeColors FileIcons { get; init; }
}
