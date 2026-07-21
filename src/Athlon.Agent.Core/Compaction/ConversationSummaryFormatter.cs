namespace Athlon.Agent.Core.Compaction;

public static class ConversationSummaryFormatter
{
    private const int MaxToolResultChars = 500;
    private const string MiddleOmitMarker = "\n\n[... middle omitted for summary budget ...]\n\n";

    public static string FormatMessages(IReadOnlyList<ChatMessage> messages)
    {
        var lines = new List<string>(messages.Count);

        foreach (var message in messages)
        {
            lines.Add(FormatMessage(message));
        }

        return string.Join("\n\n", lines);
    }

    /// <summary>
    /// Fits text into <paramref name="maxChars"/> by keeping ~40% head and the remaining tail,
    /// with an omit marker between them.
    /// </summary>
    public static string FitToMaxChars(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0 || text.Length <= maxChars)
        {
            return text;
        }

        var marker = MiddleOmitMarker;
        var markerLen = marker.Length;
        if (maxChars <= markerLen + 2)
        {
            return text[^Math.Min(maxChars, text.Length)..];
        }

        var budget = maxChars - markerLen;
        var head = Math.Max(1, budget * 40 / 100);
        var tail = Math.Max(1, budget - head);
        if (head + tail > budget)
        {
            tail = budget - head;
        }

        return text[..head] + marker + text[^tail..];
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

    private static string FormatArguments(ToolCallArguments arguments) =>
        arguments.Count == 0
            ? string.Empty
            : string.Join(", ", arguments.Select(argument => $"{argument.Key}={argument.Value.GetRawText()}"));

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value ?? string.Empty;
        }

        return value[..maxChars] + "...";
    }
}
