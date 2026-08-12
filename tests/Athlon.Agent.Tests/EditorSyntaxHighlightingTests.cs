using System.Windows.Media;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Themes;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Athlon.Agent.Tests;

public sealed class EditorSyntaxHighlightingTests
{
    [Theory]
    [InlineData("README.md")]
    [InlineData("notes.markdown")]
    [InlineData("Program.cs")]
    [InlineData("script.py")]
    [InlineData("package.json")]
    [InlineData("index.html")]
    [InlineData("app.tsx")]
    public void Resolve_returns_definition_for_common_file_types(string filePath)
    {
        AppThemeManager.Apply(AppThemeKind.Dark);

        var definition = EditorSyntaxHighlighting.Resolve(filePath);

        Assert.NotNull(definition);
        Assert.False(string.IsNullOrWhiteSpace(definition.Name));
    }

    [Fact]
    public void Resolve_remapped_html_rule_uses_themed_color()
    {
        AppThemeManager.Apply(AppThemeKind.Dark);

        var definition = EditorSyntaxHighlighting.Resolve("index.html");
        Assert.NotNull(definition);

        var htmlTagColor = FindColorByName(definition.MainRuleSet, "HtmlTag")
            ?? FindColorByName(definition.MainRuleSet, "Tags");
        Assert.NotNull(htmlTagColor);

        Assert.Equal("#E06C75", ToHex(htmlTagColor));
    }

    [Fact]
    public void Resolve_remapped_ruleset_is_not_same_instance_as_builtin()
    {
        AppThemeManager.Apply(AppThemeKind.Dark);

        var builtin = HighlightingManager.Instance.GetDefinitionByExtension(".html");
        Assert.NotNull(builtin);

        var themed = EditorSyntaxHighlighting.Resolve("index.html");
        Assert.NotNull(themed);

        Assert.NotSame(builtin.MainRuleSet, themed.MainRuleSet);
    }

    [Fact]
    public void Resolve_applies_light_theme_colors()
    {
        AppThemeManager.Apply(AppThemeKind.Light);

        var definition = EditorSyntaxHighlighting.Resolve("Program.cs");
        Assert.NotNull(definition);

        var keywordColor = FindColorByName(definition.MainRuleSet, "Keywords");
        Assert.NotNull(keywordColor);

        Assert.Equal("#1A56DB", ToHex(keywordColor));
    }

    private static HighlightingColor? FindColorByName(HighlightingRuleSet ruleSet, string name)
    {
        foreach (var color in CollectColors(ruleSet))
        {
            if (string.Equals(color.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return color;
            }
        }

        return null;
    }

    private static IEnumerable<HighlightingColor> CollectColors(HighlightingRuleSet ruleSet)
    {
        foreach (var rule in ruleSet.Rules)
        {
            if (rule.Color is not null)
            {
                yield return rule.Color;
            }
        }

        foreach (var span in ruleSet.Spans)
        {
            if (span.StartColor is not null)
            {
                yield return span.StartColor;
            }

            if (span.SpanColor is not null)
            {
                yield return span.SpanColor;
            }

            if (span.EndColor is not null)
            {
                yield return span.EndColor;
            }

            if (span.RuleSet is not null)
            {
                foreach (var nested in CollectColors(span.RuleSet))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string ToHex(HighlightingColor color)
    {
        var foreground = color.Foreground?.GetColor(null);
        Assert.NotNull(foreground);
        return AppThemeColor.ToHex(foreground.Value);
    }
}
