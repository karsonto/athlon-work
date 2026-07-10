namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Cutoff planning aligned with AgentScope <c>ConversationCompactor</c>.
/// </summary>
public static class ConversationCutoffPlanner
{
    public static bool ShouldCompact(
        IReadOnlyList<ChatMessage> messages,
        int estimatedTokens,
        ContextCompactionSettings settings,
        bool force)
    {
        if (messages.Count == 0)
        {
            return false;
        }

        if (force)
        {
            return true;
        }

        if (settings.TriggerMessages > 0 && messages.Count >= settings.TriggerMessages)
        {
            return true;
        }

        var tokenThreshold = ResolveCompactTriggerTokens(settings);
        return tokenThreshold > 0 && estimatedTokens >= tokenThreshold;
    }

    /// <summary>
    /// Effective token threshold: max of fixed <see cref="ContextCompactionSettings.TriggerTokens"/>
    /// and <see cref="ContextCompactionSettings.ContextWindowTokens"/> × <see cref="ContextCompactionSettings.CompactTriggerRatio"/>.
    /// </summary>
    public static int ResolveCompactTriggerTokens(ContextCompactionSettings settings)
    {
        var fixedThreshold = Math.Max(0, settings.TriggerTokens);
        if (settings.ContextWindowTokens <= 0 || settings.CompactTriggerRatio <= 0)
        {
            return fixedThreshold;
        }

        var windowThreshold = (int)Math.Floor(settings.ContextWindowTokens * settings.CompactTriggerRatio);
        return Math.Max(fixedThreshold, windowThreshold);
    }

    public static bool ShouldTruncateArgs(
        IReadOnlyList<ChatMessage> messages,
        int estimatedTokens,
        TruncateArgsSettings settings)
    {
        if (!settings.Enabled || messages.Count == 0)
        {
            return false;
        }

        if (settings.TriggerMessages > 0 && messages.Count >= settings.TriggerMessages)
        {
            return true;
        }

        return settings.TriggerTokens > 0 && estimatedTokens >= settings.TriggerTokens;
    }

    public static int DetermineCutoffIndex(
        IReadOnlyList<ChatMessage> messages,
        int estimatedTokens,
        ContextCompactionSettings settings,
        int? keepTokenBudgetOverride = null)
    {
        if (keepTokenBudgetOverride is > 0 && settings.DynamicCompaction.EnableSemanticCutoff)
        {
            return SemanticCutoffPlanner.DetermineCutoffIndex(messages, settings, keepTokenBudgetOverride.Value);
        }

        if (keepTokenBudgetOverride is not null)
        {
            var rawCutoff = DetermineTruncateArgsCutoffFromKeepBudget(
                messages,
                keepTokenBudgetOverride.Value,
                settings.IncludeReasoningInModelContext);
            return FindSafeCutoffPoint(messages, rawCutoff);
        }

        var rawCutoffIndex = settings.KeepTokens > 0
            ? FindTokenBasedCutoff(messages, estimatedTokens, settings.KeepTokens, settings.IncludeReasoningInModelContext)
            : FindMessageBasedCutoff(messages, settings.KeepMessages);

        return FindSafeCutoffPoint(messages, rawCutoffIndex);
    }

    public static int DetermineTruncateArgsCutoff(
        IReadOnlyList<ChatMessage> messages,
        TruncateArgsSettings settings,
        bool includeReasoningInModelContext = false)
    {
        if (settings.KeepTokens > 0)
        {
            return DetermineTruncateArgsCutoffFromKeepBudget(messages, settings.KeepTokens, includeReasoningInModelContext);
        }

        return Math.Max(0, messages.Count - settings.KeepMessages);
    }

    public static int DetermineTruncateArgsCutoffFromKeepBudget(
        IReadOnlyList<ChatMessage> messages,
        int keepTokenBudget,
        bool includeReasoningInModelContext = false)
    {
        if (keepTokenBudget <= 0 || messages.Count == 0)
        {
            return messages.Count;
        }

        var tokensKept = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var messageTokens = ContextTokenEstimator.EstimateMessage(messages[index], includeReasoningInModelContext);
            if (tokensKept + messageTokens > keepTokenBudget)
            {
                return index + 1;
            }

            tokensKept += messageTokens;
        }

        return 0;
    }

    public static int FindSafeCutoffPoint(IReadOnlyList<ChatMessage> messages, int cutoffIndex)
    {
        if (cutoffIndex <= 0 || cutoffIndex >= messages.Count)
        {
            return cutoffIndex;
        }

        if (messages[cutoffIndex].Role != MessageRole.Tool)
        {
            return cutoffIndex;
        }

        var toolCallIds = new List<string>();
        var scanIndex = cutoffIndex;
        while (scanIndex < messages.Count && messages[scanIndex].Role == MessageRole.Tool)
        {
            var toolCallId = ModelMessageBuilder.ExtractToolCallId(messages[scanIndex].Content);
            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                toolCallIds.Add(toolCallId);
            }

            scanIndex++;
        }

        if (toolCallIds.Count == 0)
        {
            return scanIndex;
        }

        for (var i = cutoffIndex - 1; i >= 0; i--)
        {
            if (messages[i].Role != MessageRole.Assistant)
            {
                continue;
            }

            var calls = AssistantToolCallsCodec.Deserialize(messages[i].ToolCallsJson);
            if (calls is not { Count: > 0 })
            {
                continue;
            }

            if (calls.Any(call => toolCallIds.Contains(call.Id, StringComparer.Ordinal)))
            {
                return i;
            }
        }

        return scanIndex;
    }

    private static int FindMessageBasedCutoff(IReadOnlyList<ChatMessage> messages, int keepMessages)
    {
        if (keepMessages <= 0 || messages.Count <= keepMessages)
        {
            return 0;
        }

        return messages.Count - keepMessages;
    }

    /// <summary>
    /// Binary search for the earliest index where the suffix fits within <paramref name="keepTokens"/>.
    /// </summary>
    private static int FindTokenBasedCutoff(
        IReadOnlyList<ChatMessage> messages,
        int totalTokens,
        int keepTokens,
        bool includeReasoningInModelContext)
    {
        if (totalTokens <= keepTokens)
        {
            return 0;
        }

        var left = 0;
        var right = messages.Count;
        var candidate = messages.Count;
        var maxIter = messages.Count > 0
            ? (int)Math.Floor(Math.Log2(messages.Count)) + 2
            : 1;

        for (var iteration = 0; iteration < maxIter && left < right; iteration++)
        {
            var mid = (left + right) / 2;
            if (ContextTokenEstimator.EstimateSuffix(messages, mid, includeReasoningInModelContext) <= keepTokens)
            {
                candidate = mid;
                right = mid;
            }
            else
            {
                left = mid + 1;
            }
        }

        return Math.Min(candidate, messages.Count - 1);
    }

}
