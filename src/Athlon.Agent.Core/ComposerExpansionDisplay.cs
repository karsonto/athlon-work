namespace Athlon.Agent.Core;

/// <summary>
/// Removes composer expansion preambles so the timeline can show the original user text.
/// </summary>
public static class ComposerExpansionDisplay
{
    public static string StripLeadingBlocks(string content, string startMarker, string endMarker)
    {
        if (string.IsNullOrEmpty(content)
            || string.IsNullOrEmpty(startMarker)
            || string.IsNullOrEmpty(endMarker))
        {
            return content;
        }

        var remaining = content;
        while (remaining.StartsWith(startMarker, StringComparison.Ordinal))
        {
            var end = remaining.IndexOf(endMarker, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            end += endMarker.Length;
            while (end < remaining.Length && remaining[end] is '\r' or '\n')
            {
                end++;
            }

            remaining = remaining[end..];
        }

        return remaining;
    }

    public static string StripTrailingPrefixedLines(string content, string linePrefix)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(linePrefix))
        {
            return content;
        }

        var remaining = content;
        while (remaining.Length > 0)
        {
            var end = remaining.Length;
            while (end > 0 && remaining[end - 1] is '\r' or '\n')
            {
                end--;
            }

            if (end == 0)
            {
                return remaining;
            }

            var lineStart = remaining.LastIndexOf('\n', end - 1);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            var line = remaining[lineStart..end];
            if (!line.StartsWith(linePrefix, StringComparison.Ordinal))
            {
                return remaining;
            }

            remaining = lineStart == 0
                ? string.Empty
                : remaining[..lineStart].TrimEnd('\r', '\n');
        }

        return remaining;
    }
}
