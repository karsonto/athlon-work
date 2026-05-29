using System.Text.RegularExpressions;

namespace Athlon.Agent.App.Services;

public static class MermaidMarkdownExtractor
{
    private static readonly Regex MermaidFenceRegex = new(
        @"```\s*mermaid\s*\r?\n([\s\S]*?)```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool ContainsMermaid(string? markdown) =>
        !string.IsNullOrWhiteSpace(markdown) && MermaidFenceRegex.IsMatch(markdown);

    public static IReadOnlyList<string> ExtractDiagrams(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Array.Empty<string>();
        }

        var matches = MermaidFenceRegex.Matches(markdown);
        if (matches.Count == 0)
        {
            return Array.Empty<string>();
        }

        var diagrams = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            if (!match.Success || match.Groups.Count < 2)
            {
                continue;
            }

            var source = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(source))
            {
                diagrams.Add(source);
            }
        }

        return diagrams;
    }
}
