namespace Athlon.Agent.Core.Compaction;

public static class ConversationSummaryFormatter
{
    private const int MaxToolResultChars = 500;

    public static string FormatMessages(IReadOnlyList<ChatMessage> messages)
    {
        var lines = new List<string>(messages.Count);

        foreach (var message in messages)
        {
            lines.Add(FormatMessage(message));
        }

        return string.Join("\n\n", lines);
    }

    private static string FormatMessage(ChatMessage message)
    {
        return message.Role switch
        {
            MessageRole.User => $"Human: {message.Content}",
            MessageRole.Assistant => FormatAssistant(message),
            MessageRole.Tool => FormatTool(message),
            _ => $"{message.Role}: {message.Content}"
        };
    }

    private static string FormatAssistant(ChatMessage message)
    {
        var parts = new List<string> { $"AI: {message.Content}" };

        var toolCalls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
        if (toolCalls is { Count: > 0 })
        {
            foreach (var call in toolCalls)
            {
                parts.Add($"[tool_call: {call.Name}({Truncate(FormatArguments(call.Arguments), MaxToolResultChars)})]");
            }
        }

        return string.Join("\n", parts);
    }

    private static string FormatTool(ChatMessage message)
    {
        return $"Tool: [tool_result] {Truncate(message.Content, MaxToolResultChars)}";
    }

    private static string FormatArguments(IReadOnlyDictionary<string, string> arguments) =>
        arguments.Count == 0
            ? string.Empty
            : string.Join(", ", arguments.Select(argument => $"{argument.Key}={argument.Value}"));

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value ?? string.Empty;
        }

        return value[..maxChars] + "...";
    }
}
