using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Keeps TURN_ACTIVITY / FILES_CHANGED replay accurate when the UI display page
/// starts mid-turn (e.g. last <see cref="ConversationDisplayLimits.PageSize"/> messages).
/// </summary>
internal static class ConversationActivitySource
{
    public const int MaxBackfillPages = 20;

    public static bool StartsAtTurnBoundary(ChatMessage message) =>
        message.Role is MessageRole.User or MessageRole.Compaction;

    public static bool NeedsTurnStartBackfill(IReadOnlyList<ChatMessage> messages) =>
        messages.Count > 0 && !StartsAtTurnBoundary(messages[0]);

    /// <summary>
    /// Prepends <paramref name="olderMessages"/> ahead of <paramref name="current"/>.
    /// </summary>
    public static List<ChatMessage> PrependOlder(
        IReadOnlyList<ChatMessage> olderMessages,
        IReadOnlyList<ChatMessage> current)
    {
        if (olderMessages.Count == 0)
        {
            return current as List<ChatMessage> ?? current.ToList();
        }

        var merged = new List<ChatMessage>(olderMessages.Count + current.Count);
        merged.AddRange(olderMessages);
        merged.AddRange(current);
        return merged;
    }

    /// <summary>
    /// Walks backward in <paramref name="sessionMessages"/> to the user/compaction that
    /// owns the first activity message, and returns messages that should be prepended.
    /// </summary>
    public static IReadOnlyList<ChatMessage> CollectTurnStartBackfill(
        IReadOnlyList<ChatMessage> sessionMessages,
        IReadOnlyList<ChatMessage> activityMessages)
    {
        if (!NeedsTurnStartBackfill(activityMessages) || sessionMessages.Count == 0)
        {
            return Array.Empty<ChatMessage>();
        }

        var firstId = activityMessages[0].Id;
        var index = -1;
        for (var i = 0; i < sessionMessages.Count; i++)
        {
            if (string.Equals(sessionMessages[i].Id, firstId, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index <= 0)
        {
            return Array.Empty<ChatMessage>();
        }

        var start = index;
        while (start > 0 && !StartsAtTurnBoundary(sessionMessages[start]))
        {
            start--;
        }

        if (start >= index)
        {
            return Array.Empty<ChatMessage>();
        }

        var existing = new HashSet<string>(
            activityMessages.Select(message => message.Id),
            StringComparer.Ordinal);
        var backfill = new List<ChatMessage>(index - start);
        for (var i = start; i < index; i++)
        {
            var message = sessionMessages[i];
            if (existing.Add(message.Id))
            {
                backfill.Add(message);
            }
        }

        return backfill;
    }
}
