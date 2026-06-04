using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Drops redundant in-memory payloads after load (e.g. base64 image data when a file path exists).
/// </summary>
internal static class ChatMessageMemorySanitizer
{
    public static AgentSession SanitizeSession(AgentSession session)
    {
        if (session.Messages.Count == 0)
        {
            return session;
        }

        var sanitized = session.Messages.Select(SanitizeMessage).ToArray();
        return ReferenceEquals(sanitized, session.Messages) || sanitized.SequenceEqual(session.Messages)
            ? session
            : session with { Messages = sanitized };
    }

    public static IReadOnlyList<ChatMessage> SanitizeMessages(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return messages;
        }

        var sanitized = messages.Select(SanitizeMessage).ToArray();
        return ReferenceEquals(sanitized, messages) || sanitized.SequenceEqual(messages)
            ? messages
            : sanitized;
    }

    private static ChatMessage SanitizeMessage(ChatMessage message)
    {
        if (message.ImageAttachments is not { Count: > 0 })
        {
            return message;
        }

        var attachments = message.ImageAttachments.Select(SanitizeImageAttachment).ToArray();
        if (attachments.SequenceEqual(message.ImageAttachments))
        {
            return message;
        }

        return message with { ImageAttachments = attachments };
    }

    private static ImageAttachment SanitizeImageAttachment(ImageAttachment attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment.LocalPath)
            || string.IsNullOrWhiteSpace(attachment.DataUrl))
        {
            return attachment;
        }

        return attachment with { DataUrl = null };
    }
}
