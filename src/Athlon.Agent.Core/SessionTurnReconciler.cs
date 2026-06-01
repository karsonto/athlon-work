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
            .Select(group => group.First())
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

            foreach (var toolCall in incompleteTools)
            {
                answeredToolCallIds.Add(toolCall.Id);
            }
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
            if (messages[index].Role == MessageRole.User)
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
            if (messages[index].Role == MessageRole.User)
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

    private static HashSet<string> BuildAnsweredToolCallIds(IReadOnlyList<ChatMessage> messages)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role != MessageRole.Tool)
            {
                continue;
            }

            var toolCallId = ExtractToolCallId(message.Content);
            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                answered.Add(toolCallId);
            }
        }

        return answered;
    }

    private static string? ExtractToolCallId(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (var line in content.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            const string prefix = "ToolCallId:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[prefix.Length..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }
}
