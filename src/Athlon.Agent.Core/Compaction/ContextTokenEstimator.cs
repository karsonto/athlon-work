namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Character-based token estimation aligned with AgentScope <c>TokenCounterUtil</c>
/// (mixed English/Chinese, conservative).
/// </summary>
public static class ContextTokenEstimator
{
    private const double CharsPerToken = 2.5;
    private const int MessageOverhead = 5;
    private const int ToolCallOverhead = 10;
    private const int ToolResultOverhead = 8;

    private static int EstimateTextTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return (int)Math.Ceiling(text.Length / CharsPerToken);
    }

    /// <summary>Public helper for budget overhead estimation.</summary>
    public static int EstimateTextTokens(string? text, double calibrationMultiplier)
    {
        var tokens = EstimateTextTokens(text);
        return calibrationMultiplier <= 0 || Math.Abs(calibrationMultiplier - 1.0) < 0.001
            ? tokens
            : (int)Math.Ceiling(tokens * calibrationMultiplier);
    }

    public static int ResolveEffectiveEstimate(
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings settings,
        ContextBudgetSnapshot? budget)
    {
        var estimated = Estimate(messages, settings.IncludeReasoningInModelContext);
        if (budget is null)
        {
            return estimated;
        }

        return Math.Max(estimated, budget.EstimatedHistory);
    }

    public static int Estimate(
        IReadOnlyList<ChatMessage> messages,
        bool includeReasoningInModelContext = false,
        double calibrationMultiplier = 1.0)
    {
        if (messages.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var message in messages)
        {
            if (message.Role == MessageRole.Compaction)
            {
                continue;
            }

            total += EstimateMessage(message, includeReasoningInModelContext);
        }

        return calibrationMultiplier <= 0 || Math.Abs(calibrationMultiplier - 1.0) < 0.001
            ? total
            : (int)Math.Ceiling(total * calibrationMultiplier);
    }

    public static int EstimateMessage(ChatMessage message, bool includeReasoningInModelContext = false)
    {
        if (message.Role == MessageRole.Compaction)
        {
            return 0;
        }

        var tokens = MessageOverhead;
        tokens += EstimateTextTokens(message.Role.ToString());

        switch (message.Role)
        {
            case MessageRole.User:
            case MessageRole.Assistant:
            case MessageRole.System:
            case MessageRole.Summary:
                tokens += EstimateTextTokens(message.Content);
                if (includeReasoningInModelContext)
                {
                    tokens += EstimateTextTokens(message.ReasoningContent);
                }

                tokens += EstimateToolCallsTokens(message.ToolCallsJson);
                break;
            case MessageRole.Tool:
                tokens += ToolResultOverhead;
                tokens += EstimateTextTokens(message.Content);
                break;
            default:
                tokens += EstimateTextTokens(message.Content);
                break;
        }

        return tokens;
    }

    public static int EstimateSuffix(
        IReadOnlyList<ChatMessage> messages,
        int startIndex,
        bool includeReasoningInModelContext = false)
    {
        if (startIndex < 0 || startIndex >= messages.Count)
        {
            return 0;
        }

        var total = 0;
        for (var i = startIndex; i < messages.Count; i++)
        {
            total += EstimateMessage(messages[i], includeReasoningInModelContext);
        }

        return total;
    }

    private static int EstimateToolCallsTokens(string? toolCallsJson)
    {
        var calls = AssistantToolCallsCodec.Deserialize(toolCallsJson);
        if (calls is not { Count: > 0 })
        {
            return 0;
        }

        var tokens = 0;
        foreach (var call in calls)
        {
            tokens += ToolCallOverhead;
            tokens += EstimateTextTokens(call.Name);
            tokens += EstimateTextTokens(call.Id);
            foreach (var argument in call.Arguments)
            {
                tokens += EstimateTextTokens(argument.Key);
                tokens += EstimateTextTokens(argument.Value);
            }
        }

        return tokens;
    }
}
