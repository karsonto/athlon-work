namespace Athlon.Agent.Core;

internal static class ModelMessageBuilder
{
    public static List<AgentModelMessage> BuildForSession(
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        bool includeReasoningInModelContext) =>
        BuildModelMessages(environmentPrompt, history, includeReasoningInModelContext);

    public static List<AgentModelMessage> BuildModelMessages(
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        bool includeReasoningInModelContext = false)
    {
        var messages = new List<AgentModelMessage>
        {
            new("system", environmentPrompt)
        };

        AppendHistoryRange(messages, history, 0, includeReasoningInModelContext);
        return messages;
    }

    public static int AppendHistoryMessage(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int index,
        bool includeReasoningInModelContext) =>
        AppendHistoryMessageCore(messages, history, index, includeReasoningInModelContext);

    private static void AppendHistoryRange(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int startIndex,
        bool includeReasoningInModelContext)
    {
        for (var index = startIndex; index < history.Count; index++)
        {
            index = AppendHistoryMessageCore(messages, history, index, includeReasoningInModelContext);
        }
    }

    private static int AppendHistoryMessageCore(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int index,
        bool includeReasoningInModelContext)
    {
        var message = history[index];
        switch (message.Role)
        {
            case MessageRole.Compaction:
                return index;
            case MessageRole.User:
                messages.Add(new AgentModelMessage("user", BuildUserContent(message)));
                return index;
            case MessageRole.Assistant:
                return AppendAssistantModelMessages(messages, history, index, includeReasoningInModelContext);
            case MessageRole.Tool:
            {
                var toolCallId = ExtractToolCallId(message.Content);
                if (toolCallId is not null)
                {
                    var stripped = StripToolCallIdAndMetadata(message.Content);
                    messages.Add(new AgentModelMessage("tool", stripped, toolCallId));
                }
                else
                {
                    messages.Add(new AgentModelMessage("user", FormatToolResultAsUserContent(message.Content)));
                }
                return index;
            }
            case MessageRole.Summary:
                messages.Add(new AgentModelMessage("user", $"History summary: {message.Content}"));
                return index;
            case MessageRole.System:
                messages.Add(new AgentModelMessage("user", message.Content));
                return index;
            default:
                messages.Add(new AgentModelMessage("user", message.Content));
                return index;
        }
    }

    public static string FormatToolResult(AgentToolCall call, ToolResult result)
    {
        var status = result.Succeeded ? "succeeded" : "failed";
        return string.Join(Environment.NewLine, new[]
        {
            $"ToolCallId: {call.Id}",
            $"Tool `{call.Name}` {status}.",
            "",
            $"Arguments: {FormatArguments(call.Arguments)}",
            $"Summary: {result.Summary}",
            "",
            result.Content ?? result.Error ?? string.Empty
        });
    }

    public static string? ExtractToolCallId(string? content)
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

    /// <summary>Strip the metadata header (ToolCallId / status / arguments / summary) from a tool result,
    /// keeping only the actual output content.</summary>
    public static string StripToolCallIdAndMetadata(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        var startIndex = 0;

        // Skip ToolCallId line
        if (lines.Length > startIndex && lines[startIndex].StartsWith("ToolCallId:", StringComparison.OrdinalIgnoreCase))
            startIndex++;
        // Skip Tool status line
        if (lines.Length > startIndex && lines[startIndex].StartsWith("Tool `", StringComparison.Ordinal))
            startIndex++;
        // Skip empty line after status
        if (lines.Length > startIndex && lines[startIndex].Length == 0)
            startIndex++;
        // Skip Arguments line
        if (lines.Length > startIndex && lines[startIndex].StartsWith("Arguments:", StringComparison.OrdinalIgnoreCase))
            startIndex++;
        // Skip Summary line
        if (lines.Length > startIndex && lines[startIndex].StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
            startIndex++;
        // Skip the trailing empty line after the metadata block
        if (lines.Length > startIndex && lines[startIndex].Length == 0)
            startIndex++;

        if (startIndex >= lines.Length)
            return string.Empty;

        return string.Join(Environment.NewLine, lines[startIndex..]);
    }

    private static int AppendAssistantModelMessages(
        List<AgentModelMessage> messages,
        IReadOnlyList<ChatMessage> history,
        int assistantIndex,
        bool includeReasoningInModelContext)
    {
        var message = history[assistantIndex];
        var reasoningContent = includeReasoningInModelContext ? message.ReasoningContent : null;
        var toolCalls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
        if (toolCalls is not { Count: > 0 })
        {
            messages.Add(new AgentModelMessage("assistant", message.Content, ReasoningContent: reasoningContent));
            return assistantIndex;
        }

        var scanIndex = assistantIndex + 1;
        var toolMessages = new List<ChatMessage>();
        while (scanIndex < history.Count)
        {
            switch (history[scanIndex].Role)
            {
                case MessageRole.Tool:
                    toolMessages.Add(history[scanIndex]);
                    scanIndex++;
                    break;
                case MessageRole.Compaction:
                    scanIndex++;
                    break;
                default:
                    goto DoneScanning;
            }
        }

        DoneScanning:
        var toolByCallId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
        foreach (var toolMessage in toolMessages)
        {
            var toolCallId = ExtractToolCallId(toolMessage.Content);
            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                toolByCallId.TryAdd(toolCallId, toolMessage);
            }
        }

        messages.Add(new AgentModelMessage("assistant", message.Content, ToolCalls: toolCalls, ReasoningContent: reasoningContent));
        foreach (var toolCall in toolCalls)
        {
            var rawContent = toolByCallId.TryGetValue(toolCall.Id, out var toolMessage)
                ? toolMessage.Content
                : "Tool did not run or the result was not recorded.";
            var content = StripToolCallIdAndMetadata(rawContent);
            messages.Add(new AgentModelMessage("tool", content, toolCall.Id));
        }

        var consumed = new HashSet<string>(toolCalls.Select(call => call.Id), StringComparer.Ordinal);
        foreach (var toolMessage in toolMessages)
        {
            var toolCallId = ExtractToolCallId(toolMessage.Content);
            if (toolCallId is not null && consumed.Contains(toolCallId))
            {
                continue;
            }

            messages.Add(new AgentModelMessage("user", FormatToolResultAsUserContent(toolMessage.Content)));
        }

        return scanIndex - 1;
    }

    private static string FormatToolResultAsUserContent(string content) =>
        string.Join(Environment.NewLine, "[Tool output]", content);

    private static object BuildUserContent(ChatMessage message)
    {
        if (message.ImageAttachments is not { Count: > 0 })
        {
            return AppendUserTimestamp(message.Content, message.CreatedAt);
        }

        var parts = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = AppendUserTimestamp(message.Content, message.CreatedAt)
            }
        };

        foreach (var image in message.ImageAttachments)
        {
            var dataUrl = ImageAttachmentDataUrlResolver.ResolveDataUrl(image);
            if (string.IsNullOrWhiteSpace(dataUrl))
            {
                continue;
            }

            parts.Add(new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>
                {
                    ["url"] = dataUrl
                }
            });
        }

        return parts;
    }

    internal static string AppendUserTimestamp(string content, DateTimeOffset createdAt)
    {
        var local = AppTimeZone.ToChina(createdAt);
        var timestamp = $"[{local:yyyy-MM-dd HH:mm} {AppTimeZone.PromptLabel}]";
        return string.IsNullOrEmpty(content)
            ? timestamp
            : $"{content}{Environment.NewLine}{Environment.NewLine}{timestamp}";
    }

    private static string FormatArguments(IReadOnlyDictionary<string, string> arguments) =>
        arguments.Count == 0
            ? "(none)"
            : string.Join(Environment.NewLine, arguments.Select(argument => $"{argument.Key}={argument.Value}"));
}
