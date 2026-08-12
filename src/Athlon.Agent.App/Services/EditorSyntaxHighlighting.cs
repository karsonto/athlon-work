using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
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
        private readonly Dictionary<HighlightingColor, HighlightingColor> _colorCache =
            new(ReferenceEqualityComparer<HighlightingColor>.Instance);
        private readonly Dictionary<HighlightingRuleSet, HighlightingRuleSet> _ruleSetCache =
            new(ReferenceEqualityComparer<HighlightingRuleSet>.Instance);
        private readonly IReadOnlyList<HighlightingColor> _namedColors =
            inner.NamedHighlightingColors.Select(RemapColor).ToArray();
        private HighlightingRuleSet? _mainRuleSet;

        public string Name => inner.Name;

        public HighlightingRuleSet MainRuleSet =>
            _mainRuleSet ??= RemapRuleSet(inner.MainRuleSet, _colorCache, _ruleSetCache);

        public IEnumerable<HighlightingColor> NamedHighlightingColors => _namedColors;

        public HighlightingRuleSet? GetNamedRuleSet(string name)
        {
            var ruleSet = inner.GetNamedRuleSet(name);
            return ruleSet is null ? null : RemapRuleSet(ruleSet, _colorCache, _ruleSetCache);
        }

        public HighlightingColor? GetNamedColor(string name)
        {
            var mapped = _namedColors.FirstOrDefault(color =>
                string.Equals(color.Name, name, StringComparison.OrdinalIgnoreCase));
            return mapped ?? RemapColor(inner.GetNamedColor(name));
        }

        public IDictionary<string, string>? Properties => inner.Properties;
    }

    private static HighlightingRuleSet RemapRuleSet(
        HighlightingRuleSet source,
        Dictionary<HighlightingColor, HighlightingColor> colorCache,
        Dictionary<HighlightingRuleSet, HighlightingRuleSet> ruleSetCache)
    {
        if (ruleSetCache.TryGetValue(source, out var cached))
        {
            return cached;
        }

        var clone = new HighlightingRuleSet { Name = source.Name };
        ruleSetCache[source] = clone;

        foreach (var rule in source.Rules)
        {
            clone.Rules.Add(new HighlightingRule
            {
                Regex = rule.Regex,
                Color = RemapColorCached(rule.Color, colorCache),
            });
        }

        foreach (var span in source.Spans)
        {
            clone.Spans.Add(new HighlightingSpan
            {
                StartExpression = span.StartExpression,
                EndExpression = span.EndExpression,
                RuleSet = span.RuleSet is null
                    ? null!
                    : RemapRuleSet(span.RuleSet, colorCache, ruleSetCache),
                StartColor = RemapColorCached(span.StartColor, colorCache),
                SpanColor = RemapColorCached(span.SpanColor, colorCache),
                EndColor = RemapColorCached(span.EndColor, colorCache),
                SpanColorIncludesStart = span.SpanColorIncludesStart,
                SpanColorIncludesEnd = span.SpanColorIncludesEnd,
            });
        }

        return clone;
    }

    private static HighlightingColor RemapColorCached(
        HighlightingColor? color,
        Dictionary<HighlightingColor, HighlightingColor> cache)
    {
        if (color is null)
        {
            return new HighlightingColor();
        }

        if (!cache.TryGetValue(color, out var mapped))
        {
            mapped = RemapColor(color);
            cache[color] = mapped;
        }

        return mapped;
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

    private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static ReferenceEqualityComparer<T> Instance { get; } = new();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

