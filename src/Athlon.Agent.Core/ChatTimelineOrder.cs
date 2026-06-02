namespace Athlon.Agent.Core;

/// <summary>
/// Chat UI timeline ordering. Session storage may place compaction audit before the kept tail
/// for model context; the UI always sorts by <see cref="ChatMessage.CreatedAt"/>.
/// </summary>
public static class ChatTimelineOrder
{
    public static IReadOnlyList<ChatMessage> OrderForDisplay(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count <= 1)
        {
            return messages;
        }

        return messages
            .Select((message, index) => (message, index))
            .OrderBy(pair => pair.message.CreatedAt)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.message)
            .ToArray();
    }
}
