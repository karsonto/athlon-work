using System.Windows.Media;

namespace Athlon.Agent.App.Themes;

/// <summary>Code editor surface and syntax token colors (VS Dark+ / Light+ style).</summary>
public sealed class EditorThemeColors
{
    public required Color Background { get; init; }
    public required Color DefaultText { get; init; }
    public required Color LineNumber { get; init; }
    public required Color SelectionBackground { get; init; }
    public required Color SelectionForeground { get; init; }
    public required Color CurrentLineBackground { get; init; }
    public required Color Link { get; init; }
    public required IReadOnlyDictionary<string, Color> SyntaxTokenColors { get; init; }
    public required IReadOnlySet<string> BoldSyntaxTokenNames { get; init; }
}
