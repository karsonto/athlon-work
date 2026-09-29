namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Rewrites tool messages for the summary model only. Mutating tools keep their body so the
/// summary can record what changed; read and search results collapse to one outcome line.
/// </summary>
public static class SummaryToolTrace
{
    private static readonly HashSet<string> VerboseToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_write",
        "file_edit",
        "apply_patch",
        "execute_command"
    };

    public static IReadOnlyList<ChatMessage> Apply(IReadOnlyList<ChatMessage> messages)
    {
        var changed = false;
        var output = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (message.Role != MessageRole.Tool
                || string.IsNullOrWhiteSpace(message.Content)
                || !TryBuildTrace(message.Content, out var trace)
                || string.Equals(trace, message.Content, StringComparison.Ordinal))
            {
                output.Add(message);
                continue;
            }

            changed = true;
            output.Add(message with { Content = trace });
        }

        return changed ? output : messages;
    }

    private static bool TryBuildTrace(string content, out string trace)
    {
        trace = content;
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? toolName = null;
        string? status = null;
        foreach (var line in lines)
        {
            if (!line.StartsWith("Tool `", StringComparison.Ordinal))
            {
                continue;
            }

            var close = line.IndexOf('`', 6);
            if (close <= 6)
            {
                return false;
            }

            toolName = line[6..close];
            status = line[(close + 1)..].Trim().TrimEnd('.');
            break;
        }

        if (string.IsNullOrWhiteSpace(toolName) || VerboseToolNames.Contains(toolName))
        {
            return false;
        }

        var locator = FindLocator(lines);
        var outcome = FindOutcome(lines);
        var builder = new System.Text.StringBuilder();
        builder.Append("Tool `").Append(toolName).Append("` ").Append(status).Append('.');
        if (!string.IsNullOrWhiteSpace(locator))
        {
            builder.Append('\n').Append(locator);
        }

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            builder.Append('\n').Append(outcome);
        }

        trace = builder.ToString();
        return true;
    }

    private static string? FindLocator(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("path=", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("command=", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("pattern=", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("query=", StringComparison.OrdinalIgnoreCase))
            {
                return line.Length <= 240 ? line : line[..240];
            }
        }

        return null;
    }

    private static string? FindOutcome(string[] lines)
    {
        string? summary = null;
        string? firstBody = null;
        var pastSummary = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            {
                summary = line["Summary:".Length..].Trim();
                pastSummary = true;
                continue;
            }

            if (!pastSummary || line.Length == 0 || line.StartsWith("Arguments:", StringComparison.Ordinal))
            {
                continue;
            }

            firstBody = line.Trim();
            break;
        }

        var outcome = string.IsNullOrWhiteSpace(firstBody) ? summary : firstBody;
        if (string.IsNullOrWhiteSpace(outcome))
        {
            return null;
        }

        return outcome.Length <= 160 ? outcome : outcome[..160];
    }
}
