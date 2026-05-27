using System.Text.Json;

namespace Athlon.Agent.Core.Compaction;

public static class ConversationCutoffPlanner
{
    public static bool ShouldCompact(
        IReadOnlyList<ChatMessage> messages,
        int estimatedTokens,
        ContextCompactionSettings settings,
        bool force)
    {
        if (force)
        {
            return messages.Count > 1;
        }

        if (messages.Count >= settings.TriggerMessages)
        {
            return true;
        }

        return settings.TriggerTokens > 0 && estimatedTokens >= settings.TriggerTokens;
    }

    public static int DetermineCutoffIndex(
        IReadOnlyList<ChatMessage> messages,
        int estimatedTokens,
        ContextCompactionSettings settings)
    {
        if (settings.KeepTokens > 0 && estimatedTokens > settings.KeepTokens)
        {
            return DetermineCutoffByTokens(messages, settings.KeepTokens);
        }

        return DetermineCutoffByMessages(messages, settings.KeepMessages);
    }

    public static int FindSafeCutoffPoint(IReadOnlyList<ChatMessage> messages, int cutoffIndex)
    {
        if (cutoffIndex <= 0 || cutoffIndex >= messages.Count)
        {
            return cutoffIndex;
        }

        if (messages[cutoffIndex].Role != MessageRole.Tool)
        {
            return cutoffIndex;
        }

        for (var i = cutoffIndex - 1; i >= 0; i--)
        {
            if (messages[i].Role == MessageRole.Assistant
                && !string.IsNullOrWhiteSpace(messages[i].ToolCallsJson))
            {
                return i;
            }
        }

        return cutoffIndex;
    }

    public static int DetermineTruncateArgsCutoff(
        IReadOnlyList<ChatMessage> messages,
        int estimatedTokens,
        TruncateArgsSettings settings)
    {
        if (settings.KeepTokens > 0 && estimatedTokens > settings.KeepTokens)
        {
            return DetermineCutoffByTokens(messages, settings.KeepTokens);
        }

        return DetermineCutoffByMessages(messages, settings.KeepMessages);
    }

    private static int DetermineCutoffByMessages(IReadOnlyList<ChatMessage> messages, int keepMessages)
    {
        if (keepMessages <= 0 || messages.Count <= keepMessages)
        {
            return 0;
        }

        return messages.Count - keepMessages;
    }

    private static int DetermineCutoffByTokens(IReadOnlyList<ChatMessage> messages, int keepTokens)
    {
        var runningTokens = 0;
        var cutoff = messages.Count;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            runningTokens += EstimateMessageTokens(messages[i]);
            if (runningTokens >= keepTokens)
            {
                cutoff = i;
                break;
            }
        }

        return cutoff;
    }

    private static int EstimateMessageTokens(ChatMessage message) =>
        Math.Max(1, JsonSerializer.Serialize(message).Length / 4);
}
