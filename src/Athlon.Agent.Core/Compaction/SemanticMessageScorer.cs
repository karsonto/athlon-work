namespace Athlon.Agent.Core.Compaction;

public static class SemanticMessageScorer
{
    private static readonly string[] MutationToolNames =
    [
        "file_write",
        "file_edit",
        "apply_patch",
        "execute_command"
    ];

    /// <summary>
    /// Facts the summary must keep: real user turns, prior compaction summaries, and mutating
    /// tool results. Read and search output is left to the structured tool trace.
    /// </summary>
    public static bool ShouldPreserveInSummary(ChatMessage message)
    {
        if (SummaryMessageBuilder.IsSummaryMessage(message))
        {
            return true;
        }

        if (message.Role == MessageRole.User)
        {
            return true;
        }

        if (message.Role != MessageRole.Tool)
        {
            return false;
        }

        var content = message.Content ?? string.Empty;
        foreach (var name in MutationToolNames)
        {
            if (content.Contains($"Tool `{name}`", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
