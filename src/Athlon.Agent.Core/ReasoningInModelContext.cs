namespace Athlon.Agent.Core;

/// <summary>
/// Chooses when assistant <c>ReasoningContent</c> is sent to the model and counted in history estimates.
/// Tool-call messages always include reasoning; plain replies require the include-all flag.
/// </summary>
internal static class ReasoningInModelContext
{
    public static string? Select(string? reasoningContent, bool includeAll, bool hasToolCalls)
    {
        if (string.IsNullOrEmpty(reasoningContent))
        {
            return null;
        }

        return includeAll || hasToolCalls ? reasoningContent : null;
    }

    public static bool CountsTowardEstimate(ChatMessage message, bool includeAll)
    {
        if (string.IsNullOrEmpty(message.ReasoningContent))
        {
            return false;
        }

        if (includeAll)
        {
            return true;
        }

        return message.Role == MessageRole.Assistant
            && AssistantToolCallsCodec.Deserialize(message.ToolCallsJson) is { Count: > 0 };
    }
}
