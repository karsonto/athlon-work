using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Trims heavy tool result bodies for UI display pages while keeping file-edit diffs
/// so FILES_CHANGED cards can be rebuilt on a cold session load.
/// </summary>
internal static class ConversationDisplayContentStripper
{
    private static readonly HashSet<string> KeepBodyFileTools = new(StringComparer.Ordinal)
    {
        "file_edit",
        "file_write",
        "apply_patch"
    };

    public static ChatMessage StripToolContentForDisplay(ChatMessage message)
    {
        if (message.Role != MessageRole.Tool)
        {
            return message;
        }

        var content = message.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return message;
        }

        if (KeepBodyFileTools.Contains(ExtractToolName(content)))
        {
            return message;
        }

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var resultLines = new List<string>(capacity: 6);
        var passedSummary = false;

        foreach (var line in lines)
        {
            resultLines.Add(line);

            if (line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                passedSummary = true;
            }
            else if (passedSummary && line.Length == 0)
            {
                break;
            }
        }

        return message with { Content = string.Join("\n", resultLines) };
    }

    internal static string ExtractToolName(string content)
    {
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            const string prefix = "Tool `";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var start = prefix.Length;
            var end = line.IndexOf('`', start);
            if (end > start)
            {
                return line[start..end];
            }
        }

        return string.Empty;
    }
}
