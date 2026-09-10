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
    private const int ImageAttachmentEstimate = 900;

    private static int EstimateRawTextTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return (int)Math.Ceiling(text.Length / CharsPerToken);
    }

    /// <summary>Public helper for budget overhead estimation.</summary>
    public static int EstimateTextTokens(string? text, double calibrationMultiplier = 1.0)
    {
        var tokens = EstimateRawTextTokens(text);
        return calibrationMultiplier <= 0 || Math.Abs(calibrationMultiplier - 1.0) < 0.001
            ? tokens
            : (int)Math.Ceiling(tokens * calibrationMultiplier);
    }

    public static int EstimateCharacterBudget(int tokens) =>
        tokens <= 0 ? 0 : (int)Math.Floor(tokens * CharsPerToken);

    public static int EstimateModelRequest(AgentModelRequest request)
    {
        var total = request.Messages.Sum(EstimateModelMessage);
        foreach (var tool in request.Tools)
        {
            total += ToolCallOverhead;
            total += EstimateRawTextTokens(tool.Name);
            total += EstimateRawTextTokens(tool.Description);
            total += EstimateRawTextTokens(tool.ParametersSchema.ToCanonicalJson());
        }
        return total;
    }

    public static int EstimateModelResponse(AgentModelResponse response)
    {
        var total = EstimateRawTextTokens(response.Content) + EstimateRawTextTokens(response.ReasoningContent);
        foreach (var call in response.ToolCalls)
        {
            total += ToolCallOverhead + EstimateRawTextTokens(call.Name) + EstimateRawTextTokens(call.Id);
            total += EstimateRawTextTokens(call.Arguments.ToJsonString());
        }
        return total;
    }

    public static int EstimateModelMessage(AgentModelMessage message)
    {
        var total = MessageOverhead
            + EstimateRawTextTokens(message.Role)
            + EstimateRawTextTokens(message.Content?.ToString())
            + EstimateRawTextTokens(message.ReasoningContent);
        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var call in message.ToolCalls)
            {
                total += ToolCallOverhead + EstimateRawTextTokens(call.Name) + EstimateRawTextTokens(call.Id);
                total += EstimateRawTextTokens(call.Arguments.ToJsonString());
            }
        }
        return total;
    }

    public static int ResolveEffectiveEstimate(
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings settings,
        ContextBudgetSnapshot? budget,
        int? knownRawHistoryEstimate = null)
    {
        var estimated = knownRawHistoryEstimate
            ?? Estimate(
                messages,
                settings.IncludeReasoningInModelContext,
                maxToolScreenshots: settings.MaxToolScreenshotsInModelContext,
                hygiene: settings.RequestHistoryHygiene);
        if (budget is null)
        {
            return estimated;
        }

        return Math.Max(estimated, budget.EstimatedHistory);
    }

    public static int Estimate(
        IReadOnlyList<ChatMessage> messages,
        bool includeReasoningInModelContext = false,
        double calibrationMultiplier = 1.0,
        int maxToolScreenshots = int.MaxValue,
        RequestHistoryHygieneSettings? hygiene = null)
    {
        if (messages.Count == 0)
        {
            return 0;
        }

        var pairedToolCallIds = ResolvePairedToolCallIds(messages, hygiene);
        var remainingToolScreenshots = Math.Max(0, maxToolScreenshots);
        var total = 0;
        // Newest-first allocation for Tool screenshots (matches RetainLatestToolScreenshots).
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Role == MessageRole.Compaction)
            {
                continue;
            }

            total += EstimateMessageCore(
                message,
                includeReasoningInModelContext,
                ref remainingToolScreenshots,
                hygiene,
                pairedToolCallIds);
        }

        return calibrationMultiplier <= 0 || Math.Abs(calibrationMultiplier - 1.0) < 0.001
            ? total
            : (int)Math.Ceiling(total * calibrationMultiplier);
    }

    public static int EstimateMessage(
        ChatMessage message,
        bool includeReasoningInModelContext = false,
        RequestHistoryHygieneSettings? hygiene = null)
    {
        var unlimited = int.MaxValue;
        return EstimateMessageCore(
            message,
            includeReasoningInModelContext,
            ref unlimited,
            hygiene,
            pairedToolCallIds: null);
    }

    public static int EstimateMessage(
        ChatMessage message,
        bool includeReasoningInModelContext,
        ref int remainingToolScreenshots) =>
        EstimateMessageCore(
            message,
            includeReasoningInModelContext,
            ref remainingToolScreenshots,
            hygiene: null,
            pairedToolCallIds: null);

    private static int EstimateMessageCore(
        ChatMessage message,
        bool includeReasoningInModelContext,
        ref int remainingToolScreenshots,
        RequestHistoryHygieneSettings? hygiene,
        HashSet<string>? pairedToolCallIds)
    {
        if (message.Role == MessageRole.Compaction)
        {
            return 0;
        }

        var tokens = MessageOverhead;
        tokens += EstimateRawTextTokens(message.Role.ToString());

        switch (message.Role)
        {
            case MessageRole.User:
            case MessageRole.Assistant:
            case MessageRole.System:
            case MessageRole.Summary:
                tokens += IsToolPayloadMessage(message, hygiene)
                    ? EstimateClampedToolResultTokens(message.Content, hygiene)
                    : EstimateRawTextTokens(message.Content);
                if (ReasoningInModelContext.CountsTowardEstimate(message, includeReasoningInModelContext))
                {
                    tokens += EstimateRawTextTokens(message.ReasoningContent);
                }

                tokens += EstimateToolCallsTokens(message.ToolCallsJson, hygiene, pairedToolCallIds);
                break;
            case MessageRole.Tool:
                tokens += ToolResultOverhead;
                tokens += EstimateClampedToolResultTokens(message.Content, hygiene);
                break;
            default:
                tokens += EstimateRawTextTokens(message.Content);
                break;
        }

        if (message.ImageAttachments is { Count: > 0 } images)
        {
            var imageCount = message.Role == MessageRole.Tool
                ? Math.Min(images.Count, Math.Max(0, remainingToolScreenshots))
                : images.Count;
            if (message.Role == MessageRole.Tool)
            {
                remainingToolScreenshots -= imageCount;
            }

            tokens += imageCount * ImageAttachmentEstimate;
        }

        return tokens;
    }

    public static int EstimateSuffix(
        IReadOnlyList<ChatMessage> messages,
        int startIndex,
        bool includeReasoningInModelContext = false,
        int maxToolScreenshots = int.MaxValue,
        RequestHistoryHygieneSettings? hygiene = null)
    {
        if (startIndex < 0 || startIndex >= messages.Count)
        {
            return 0;
        }

        var pairedToolCallIds = ResolvePairedToolCallIds(messages, hygiene);
        var remainingToolScreenshots = Math.Max(0, maxToolScreenshots);
        var total = 0;
        for (var i = messages.Count - 1; i >= startIndex; i--)
        {
            var message = messages[i];
            if (message.Role == MessageRole.Compaction)
            {
                continue;
            }

            total += EstimateMessageCore(
                message,
                includeReasoningInModelContext,
                ref remainingToolScreenshots,
                hygiene,
                pairedToolCallIds);
        }

        return total;
    }

    /// <summary>
    /// Tool-call ids that have a matching Tool message in <paramref name="messages"/>.
    /// Mirrors <see cref="RequestHistoryHygiene.ApplyToModelMessages"/>'s pairing so argument
    /// clamping never touches calls that hygiene would leave untouched.
    /// </summary>
    private static HashSet<string>? ResolvePairedToolCallIds(
        IReadOnlyList<ChatMessage> messages,
        RequestHistoryHygieneSettings? hygiene)
    {
        if (hygiene is not { Enabled: true })
        {
            return null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role != MessageRole.Tool || ModelMessageBuilder.IsRunningToolResult(message.Content))
            {
                continue;
            }

            var id = ModelMessageBuilder.ExtractToolCallId(message.Content);
            if (!string.IsNullOrWhiteSpace(id))
            {
                ids.Add(id!);
            }
        }

        return ids;
    }

    /// <summary>
    /// True when hygiene treats this message as a tool payload (role Tool, or a user message
    /// carrying a formatted tool output). Clamping is a conservative upper bound on hygiene's output.
    /// </summary>
    private static bool IsToolPayloadMessage(ChatMessage message, RequestHistoryHygieneSettings? hygiene)
    {
        if (hygiene is not { Enabled: true })
        {
            return false;
        }

        if (message.Role == MessageRole.Tool)
        {
            return true;
        }

        if (message.Role != MessageRole.User || string.IsNullOrEmpty(message.Content))
        {
            return false;
        }

        return message.Content.StartsWith("[Tool output]", StringComparison.Ordinal)
            || message.Content.Contains("ToolCallId:", StringComparison.OrdinalIgnoreCase);
    }

    private static int EstimateClampedToolResultTokens(string? content, RequestHistoryHygieneSettings? hygiene)
    {
        var tokens = EstimateRawTextTokens(content);
        if (hygiene is not { Enabled: true })
        {
            return tokens;
        }

        return Math.Min(tokens, Math.Max(0, hygiene.MaxToolResultTokens));
    }

    private static int EstimateToolCallsTokens(
        string? toolCallsJson,
        RequestHistoryHygieneSettings? hygiene,
        HashSet<string>? pairedToolCallIds)
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
            tokens += EstimateRawTextTokens(call.Name);
            tokens += EstimateRawTextTokens(call.Id);
            var clampArguments = hygiene is { Enabled: true }
                && pairedToolCallIds is not null
                && pairedToolCallIds.Contains(call.Id);
            foreach (var argument in call.Arguments)
            {
                tokens += EstimateRawTextTokens(argument.Key);
                tokens += clampArguments
                    ? EstimateClampedArgumentTokens(argument.Key, argument.Value, hygiene!)
                    : EstimateRawTextTokens(argument.Value.GetRawText());
            }
        }

        return tokens;
    }

    private static int EstimateClampedArgumentTokens(
        string key,
        System.Text.Json.JsonElement value,
        RequestHistoryHygieneSettings hygiene)
    {
        var rawTokens = EstimateRawTextTokens(value.GetRawText());
        // Hygiene only rewrites plain string arguments that are not continuity-critical.
        if (value.ValueKind != System.Text.Json.JsonValueKind.String
            || RequestHistoryHygiene.IsContinuityArgument(key))
        {
            return rawTokens;
        }

        var text = value.GetString() ?? string.Empty;
        var textTokens = EstimateRawTextTokens(text);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes <= hygiene.MaxToolArgumentStringBytes && textTokens <= hygiene.MaxToolArgumentStringTokens)
        {
            // Left untouched: the payload carries the original raw JSON.
            return rawTokens;
        }

        // Rewritten to a short omission notice; MaxToolArgumentStringTokens is a safe upper bound.
        return Math.Min(rawTokens, Math.Max(0, hygiene.MaxToolArgumentStringTokens));
    }
}
