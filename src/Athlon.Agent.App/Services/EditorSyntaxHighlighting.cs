using System.IO;
using System.Windows;
using System.Windows.Media;
using Athlon.Agent.App.Themes;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Athlon.Agent.App.Services;

/// <summary>Editor syntax highlighting using colors from <see cref="AppThemeManager"/>.</summary>
public static class EditorSyntaxHighlighting
{
    private static EditorThemeColors Editor => AppThemeManager.Current.Editor;

    public static Color EditorBackground => Editor.Background;
    public static Color DefaultText => Editor.DefaultText;
    public static Color LineNumber => Editor.LineNumber;
    public static Color SelectionBackground => Editor.SelectionBackground;
    public static Color SelectionForeground => Editor.SelectionForeground;
    public static Color CurrentLineBackground => Editor.CurrentLineBackground;
    public static Color Link => Editor.Link;

    private static readonly Dictionary<string, string[]> ExtensionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["markdown"] = ["md"],
        ["csx"] = ["cs"],
        ["tsx"] = ["js"],
        ["jsx"] = ["js"],
        ["mjs"] = ["js"],
        ["cjs"] = ["js"],
        ["pyw"] = ["py"],
        ["hpp"] = ["cpp"],
        ["cc"] = ["cpp"],
        ["cxx"] = ["cpp"],
        ["h"] = ["cpp"],
        ["yml"] = ["xml"],
        ["yaml"] = ["xml"],
        ["toml"] = ["xml"],
        ["ini"] = ["xml"],
        ["cfg"] = ["xml"],
        ["conf"] = ["xml"],
        ["dockerfile"] = ["xml"],
        ["sh"] = ["xml"],
        ["bash"] = ["xml"],
        ["zsh"] = ["xml"],
        ["ps1"] = ["ps1"],
        ["psm1"] = ["ps1"],
        ["psd1"] = ["ps1"],
        ["razor"] = ["html"],
        ["cshtml"] = ["html"],
        ["ipynb"] = ["json"],
    };

    public static IHighlightingDefinition? Resolve(string filePath)
    {
        var extension = NormalizeExtension(Path.GetExtension(filePath));
        if (extension.Length == 0)
        {
            return null;
        }

        var definition = HighlightingManager.Instance.GetDefinitionByExtension(extension);
        if (definition is null && ExtensionAliases.TryGetValue(extension.TrimStart('.'), out var aliases))
        {
            foreach (var alias in aliases)
            {
                definition = HighlightingManager.Instance.GetDefinitionByExtension(NormalizeExtension(alias));
                if (definition is not null)
                {
                    break;
                }
            }
        }

        return definition is null ? null : new ThemedHighlightingDefinition(definition);
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        extension = extension.Trim();
        return extension.StartsWith('.') ? extension : "." + extension;
    }

    private sealed class ThemedHighlightingDefinition(IHighlightingDefinition inner) : IHighlightingDefinition
    {
        private readonly IReadOnlyList<HighlightingColor> _namedColors =
            inner.NamedHighlightingColors.Select(RemapColor).ToArray();

        public string Name => inner.Name;

        public HighlightingRuleSet MainRuleSet => inner.MainRuleSet;

        public IEnumerable<HighlightingColor> NamedHighlightingColors => _namedColors;

        public HighlightingRuleSet? GetNamedRuleSet(string name) => inner.GetNamedRuleSet(name);

        public HighlightingColor? GetNamedColor(string name)
        {
            var mapped = _namedColors.FirstOrDefault(color =>
                string.Equals(color.Name, name, StringComparison.OrdinalIgnoreCase));
            return mapped ?? RemapColor(inner.GetNamedColor(name));
        }

        public IDictionary<string, string>? Properties => inner.Properties;
    }

    private static HighlightingColor RemapColor(HighlightingColor? color)
    {
        if (color is null)
        {
            return new HighlightingColor();
        }

        var editor = Editor;
        var mapped = new HighlightingColor
        {
            Name = color.Name,
            FontWeight = color.FontWeight,
            FontStyle = color.FontStyle,
            Underline = color.Underline,
            Strikethrough = color.Strikethrough,
        };

        if (TryMapNamedColor(color.Name, editor, out var foreground))
        {
            mapped.Foreground = ToHighlightingBrush(foreground);
            if (editor.BoldSyntaxTokenNames.Contains(color.Name) && mapped.FontWeight is null)
            {
                mapped.FontWeight = FontWeights.Bold;
            }
        }
        else
        {
            mapped.Foreground = ToHighlightingBrush(editor.DefaultText);
        }

        if (color.Background is not null)
        {
            var bg = color.Background.GetColor(null);
            mapped.Background = bg is null ? null : ToHighlightingBrush(bg.Value);
        }

        return mapped;
    }

    private static bool TryMapNamedColor(string? name, EditorThemeColors editor, out Color foreground)
    {
        if (!string.IsNullOrWhiteSpace(name) && editor.SyntaxTokenColors.TryGetValue(name, out foreground))
        {
            return true;
        }

        foreground = editor.DefaultText;
        return false;
    }

    private static HighlightingBrush ToHighlightingBrush(Color color) => new SimpleHighlightingBrush(color);
}
