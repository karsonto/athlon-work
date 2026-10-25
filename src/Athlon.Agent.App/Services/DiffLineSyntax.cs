using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Athlon.Agent.App.Services;

public readonly record struct DiffSyntaxToken(string Text, Color? Foreground);

/// <summary>Colors one diff line with the editor's AvalonEdit definition for that path.</summary>
public static class DiffLineSyntax
{
    public static IReadOnlyList<DiffSyntaxToken> Tokenize(string? path, string text)
    {
        if (text.Length == 0)
        {
            return Array.Empty<DiffSyntaxToken>();
        }

        var definition = Resolve(path);
        if (definition is null)
        {
            return new[] { new DiffSyntaxToken(text, null) };
        }

        try
        {
            var document = new TextDocument(text);
            var highlighter = new DocumentHighlighter(document, definition);
            return Slice(text, highlighter.HighlightLine(1).Sections);
        }
        catch (ArgumentException)
        {
            return new[] { new DiffSyntaxToken(text, null) };
        }
    }

    private static IHighlightingDefinition? Resolve(string? path)
    {
        var definition = EditorSyntaxHighlighting.Resolve(path ?? string.Empty);
        if (definition is not null || string.IsNullOrWhiteSpace(path))
        {
            return definition;
        }

        // AvalonEdit has no .xaml definition; the XML rules color the same markup.
        return path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            ? EditorSyntaxHighlighting.Resolve("file.xml")
            : null;
    }

    private static IReadOnlyList<DiffSyntaxToken> Slice(string text, IList<HighlightedSection> sections)
    {
        var tokens = new List<DiffSyntaxToken>();
        var index = 0;
        foreach (var section in sections)
        {
            if (section.Length <= 0)
            {
                continue;
            }

            var start = Math.Clamp(section.Offset, 0, text.Length);
            var end = Math.Clamp(section.Offset + section.Length, start, text.Length);
            if (start > index)
            {
                tokens.Add(new DiffSyntaxToken(text[index..start], null));
            }

            if (end > start)
            {
                tokens.Add(new DiffSyntaxToken(text[start..end], Foreground(section.Color)));
            }

            index = Math.Max(index, end);
        }

        if (index < text.Length)
        {
            tokens.Add(new DiffSyntaxToken(text[index..], null));
        }

        return tokens;
    }

    private static Color? Foreground(HighlightingColor? color)
    {
        var brush = color?.Foreground;
        if (brush is null)
        {
            return null;
        }

        return brush.GetColor(null!);
    }
}
