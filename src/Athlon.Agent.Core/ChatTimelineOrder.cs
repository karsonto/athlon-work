namespace Athlon.Agent.Core;

/// <summary>
/// Chat UI timeline ordering. Session storage may place compaction audit before the kept tail
/// for model context; the UI always sorts by <see cref="ChatMessage.CreatedAt"/>.
/// Ties break on <see cref="ChatMessage.Id"/> rather than input position so the rendered
/// timeline is identical no matter how the transcript was assembled (live stream vs. persisted
/// replay vs. a fresh process). A positional tie-break would let concurrent messages that share
/// a timestamp swap places between a live render and a reload.
/// </summary>
public static class ChatTimelineOrder
{
    public static IReadOnlyList<ChatMessage> OrderForDisplay(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count <= 1)
        {
            return messages;
        }

        var alreadyOrdered = true;
        for (var i = 1; i < messages.Count; i++)
        {
            var previous = messages[i - 1];
            var current = messages[i];
            if (current.CreatedAt > previous.CreatedAt)
            {
                continue;
            }

            if (current.CreatedAt == previous.CreatedAt
                && string.CompareOrdinal(current.Id, previous.Id) > 0)
            {
                continue;
            }

            alreadyOrdered = false;
            break;
        }

        if (alreadyOrdered)
        {
            return messages;
        }

        return messages
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
