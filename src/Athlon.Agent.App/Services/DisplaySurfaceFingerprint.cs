using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.App.Services;

public sealed record DisplaySurfaceFingerprint(
    string? FirstDisplayMessageId,
    string? LastDisplayMessageId,
    int DisplayCount,
    string? FirstActivityMessageId,
    string? LastActivityMessageId,
    int ActivityCount,
    long? OlderCursorByteOffset,
    int CompactionMessageCount,
    int SummaryMessageCount)
{
    public static DisplaySurfaceFingerprint Empty { get; } =
        new(null, null, 0, null, null, 0, null, 0, 0);

    public static DisplaySurfaceFingerprint From(
        IReadOnlyList<ChatMessage> displayMessages,
        IReadOnlyList<ChatMessage> activityMessages,
        ConversationDisplayCursor? olderCursor)
    {
        static string? FirstId(IReadOnlyList<ChatMessage> messages) => messages.Count > 0 ? messages[0].Id : null;
        static string? LastId(IReadOnlyList<ChatMessage> messages) => messages.Count > 0 ? messages[^1].Id : null;

        return new DisplaySurfaceFingerprint(
            FirstId(displayMessages),
            LastId(displayMessages),
            displayMessages.Count,
            FirstId(activityMessages),
            LastId(activityMessages),
            activityMessages.Count,
            olderCursor?.ByteOffset,
            activityMessages.Count(message => message.Role == MessageRole.Compaction),
            activityMessages.Count(message => SummaryMessageBuilder.IsSummaryMessage(message)));
    }
}
