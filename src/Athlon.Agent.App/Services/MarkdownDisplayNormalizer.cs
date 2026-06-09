using System.Text;
using System.Text.RegularExpressions;

namespace Athlon.Agent.App.Services;

public sealed record FencedBlockInfo(string? Language, string Content);

/// <summary>
/// Prepares assistant markdown for display without mutating persisted message content.
/// </summary>
public static partial class MarkdownDisplayNormalizer
{
    [GeneratedRegex(@"^[-_*]{3,}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ThematicBreakRegex();

    [GeneratedRegex(@"^```([\w-]+)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FenceLineRegex();

    public static string NormalizeForDisplay(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>(lines.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (IsThematicBreak(line) && IsAdjacentToFence(lines, i))
            {
                continue;
            }

            output.Add(line);
        }

        return string.Join('\n', output);
    }

    public static IReadOnlyList<FencedBlockInfo> ExtractFencedBlocks(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return Array.Empty<FencedBlockInfo>();
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var blocks = new List<FencedBlockInfo>();
        var inFence = false;
        string? language = null;
        var content = new StringBuilder();

        foreach (var line in lines)
        {
            if (!inFence)
            {
                var openMatch = FenceLineRegex().Match(line);
                if (openMatch.Success)
                {
                    inFence = true;
                    language = NormalizeLanguage(openMatch.Groups[1].Value);
                    content.Clear();
                }

                continue;
            }

            if (FenceLineRegex().IsMatch(line))
            {
                blocks.Add(new FencedBlockInfo(language, content.ToString().TrimEnd('\n', '\r')));
                inFence = false;
                language = null;
                content.Clear();
                continue;
            }

            if (content.Length > 0)
            {
                content.Append('\n');
            }

            content.Append(line);
        }

        if (inFence)
        {
            blocks.Add(new FencedBlockInfo(language, content.ToString().TrimEnd('\n', '\r')));
        }

        return blocks;
    }

    private static bool IsThematicBreak(string line) => ThematicBreakRegex().IsMatch(line);

    private static bool IsFenceLine(string line) => FenceLineRegex().IsMatch(line);

    private static bool IsAdjacentToFence(string[] lines, int thematicBreakIndex)
    {
        if (thematicBreakIndex > 0 && IsFenceLine(lines[thematicBreakIndex - 1]))
        {
            return true;
        }

        if (thematicBreakIndex < lines.Length - 1 && IsFenceLine(lines[thematicBreakIndex + 1]))
        {
            return true;
        }

        if (thematicBreakIndex > 1
            && string.IsNullOrWhiteSpace(lines[thematicBreakIndex - 1])
            && IsFenceLine(lines[thematicBreakIndex - 2]))
        {
            return true;
        }

        if (thematicBreakIndex < lines.Length - 2
            && string.IsNullOrWhiteSpace(lines[thematicBreakIndex + 1])
            && IsFenceLine(lines[thematicBreakIndex + 2]))
        {
            return true;
        }

        return false;
    }

    private static string? NormalizeLanguage(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
