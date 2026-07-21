using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public sealed record SessionTurnEndSnapshot(
    string? AssistantContent,
    string? AssistantReasoning,
    IReadOnlyList<AgentToolCall> IncompleteToolCalls,
    bool WasCancelled,
    bool TimedOut,
    string? ErrorMessage);

public sealed record SessionTurnReconcileResult(AgentSession Session, IReadOnlyList<ChatMessage> PersistedMessages);

public static class SessionTurnReconciler
{
    public static SessionTurnReconcileResult Reconcile(AgentSession session, SessionTurnEndSnapshot snapshot)
    {
        if (!NeedsReconcile(snapshot))
        {
            return new SessionTurnReconcileResult(session, Array.Empty<ChatMessage>());
        }

        var persisted = new List<ChatMessage>();
        var messages = session.Messages.ToList();
        var parentId = FindLastUserMessageId(messages);
        var answeredToolCallIds = BuildAnsweredToolCallIds(messages);

        var incompleteTools = snapshot.IncompleteToolCalls
            .Where(call => !string.IsNullOrWhiteSpace(call.Id))
            .Where(call => !answeredToolCallIds.Contains(call.Id))
            .GroupBy(call => call.Id, StringComparer.Ordinal)
            .Select(group =>
            {
                if (group.Count() > 1)
                {
                    // Duplicate toolCallId detected — use the last instance
                    // (likely has the most complete arguments)
                    return group.Last();
                }
                return group.Single();
            })
            .ToList();

        var hasAssistantText = !string.IsNullOrWhiteSpace(snapshot.AssistantContent)
            || !string.IsNullOrWhiteSpace(snapshot.AssistantReasoning);
        var tailAssistant = FindTailAssistantAfterLastUser(messages);

        if (tailAssistant is null && (hasAssistantText || incompleteTools.Count > 0))
        {
            var content = snapshot.AssistantContent ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content) && snapshot.WasCancelled)
            {
                content = "（生成已停止）";
            }

            var assistant = ChatMessage.Create(
                MessageRole.Assistant,
                content,
                parentId,
                incompleteTools.Count > 0 ? incompleteTools : null,
                snapshot.AssistantReasoning);
            messages.Add(assistant);
            persisted.Add(assistant);
        }

        foreach (var toolCall in incompleteTools)
        {
            if (answeredToolCallIds.Contains(toolCall.Id))
            {
                continue;
            }

            var toolMessage = ChatMessage.Create(
                MessageRole.Tool,
                AgentRuntime.FormatToolResult(toolCall, BuildInterruptedToolResult(snapshot)),
                parentId);
            messages.Add(toolMessage);
            persisted.Add(toolMessage);
            answeredToolCallIds.Add(toolCall.Id);
        }

        var notice = BuildTurnNotice(snapshot);
        if (!string.IsNullOrWhiteSpace(notice))
        {
            var systemMessage = ChatMessage.Create(MessageRole.System, notice, parentId);
            messages.Add(systemMessage);
            persisted.Add(systemMessage);
        }

        if (persisted.Count == 0)
        {
            return new SessionTurnReconcileResult(session, Array.Empty<ChatMessage>());
        }

        return new SessionTurnReconcileResult(session.WithMessages(messages), persisted);
    }

    private static bool NeedsReconcile(SessionTurnEndSnapshot snapshot) =>
        snapshot.WasCancelled
        || snapshot.TimedOut
        || !string.IsNullOrWhiteSpace(snapshot.ErrorMessage);

    private static ToolResult BuildInterruptedToolResult(SessionTurnEndSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            return ToolResult.Failure(
                "工具未完成",
                $"模型调用失败，工具未执行完成：{snapshot.ErrorMessage}");
        }

        if (snapshot.TimedOut)
        {
            return ToolResult.Failure(
                "工具未完成",
                "本回合因超时被自动停止，工具未执行完成。");
        }

        return ToolResult.Failure(
            "工具未完成",
            "上次对话在工具执行或生成时被用户停止。可继续发送消息让助手接着处理。");
    }

    private static string? BuildTurnNotice(SessionTurnEndSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            return snapshot.ErrorMessage;
        }

        if (snapshot.TimedOut)
        {
            return "本回合已超过配置的超时时间，已自动停止。";
        }

        if (snapshot.WasCancelled)
        {
            return "生成已停止。";
        }

        return null;
    }

    private static string? FindLastUserMessageId(IReadOnlyList<ChatMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (IsRealUserMessage(messages[index]))
            {
                return messages[index].Id;
            }
        }

        return null;
    }

    private static ChatMessage? FindTailAssistantAfterLastUser(IReadOnlyList<ChatMessage> messages)
    {
        var lastUserIndex = -1;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (IsRealUserMessage(messages[index]))
            {
                lastUserIndex = index;
                break;
            }
        }

        if (lastUserIndex < 0)
        {
            return null;
        }

        for (var index = lastUserIndex + 1; index < messages.Count; index++)
        {
            if (messages[index].Role == MessageRole.Assistant)
            {
                return messages[index];
            }
        }

        return null;
    }

    private static bool IsRealUserMessage(ChatMessage message) =>
        message.Role == MessageRole.User && !SummaryMessageBuilder.IsSummaryMessage(message);

    private static HashSet<string> BuildAnsweredToolCallIds(IReadOnlyList<ChatMessage> messages)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role != MessageRole.Tool)
            {
                continue;
            }

            var toolCallId = ModelMessageBuilder.ExtractToolCallId(message.Content);
            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                answered.Add(toolCallId);
            }
        }

        return answered;
    }

}
