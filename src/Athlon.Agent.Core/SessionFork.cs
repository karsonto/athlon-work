namespace Athlon.Agent.Core;

/// <summary>
/// A fork keeps every message before a chosen user message and returns that message for the composer.
/// </summary>
public sealed record SessionForkSlice(IReadOnlyList<ChatMessage> Prefix, ChatMessage ComposerMessage);

public static class SessionFork
{
    public const string TitleSuffix = " 的分叉";

    public static SessionForkSlice? TrySlice(IReadOnlyList<ChatMessage> messages, string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId) || messages.Count == 0)
        {
            return null;
        }

        for (var index = 0; index < messages.Count; index++)
        {
            if (!string.Equals(messages[index].Id, messageId, StringComparison.Ordinal))
            {
                continue;
            }

            if (messages[index].Role != MessageRole.User)
            {
                return null;
            }

            var prefix = new ChatMessage[index];
            for (var i = 0; i < index; i++)
            {
                prefix[i] = messages[i];
            }

            return new SessionForkSlice(prefix, messages[index]);
        }

        return null;
    }

    public static AgentSession CreateSession(AgentSession source, IReadOnlyList<ChatMessage> prefix)
    {
        var title = string.IsNullOrWhiteSpace(source.Title)
            ? "分叉"
            : source.Title.Trim() + TitleSuffix;
        return new AgentSession(
            IdGen.NewId(),
            title,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            source.ActiveWorkspace,
            source.ActiveSkill,
            source.ModelName,
            prefix)
        {
            ActiveWorkspaceId = source.ActiveWorkspaceId
        };
    }

    public static IReadOnlyList<string> ToolCallIds(IReadOnlyList<ChatMessage> messages)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            var calls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
            if (calls is not null)
            {
                foreach (var call in calls)
                {
                    Add(seen, ids, call.Id);
                }
            }

            if (message.Role == MessageRole.Tool)
            {
                Add(seen, ids, ModelMessageBuilder.ExtractToolCallId(message.Content));
                if (message.Id.StartsWith("tool:", StringComparison.Ordinal) && message.Id.Length > "tool:".Length)
                {
                    Add(seen, ids, message.Id["tool:".Length..]);
                }
            }
        }

        return ids;
    }

    private static void Add(HashSet<string> seen, List<string> ids, string? toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId) || !seen.Add(toolCallId))
        {
            return;
        }

        ids.Add(toolCallId);
    }
}
