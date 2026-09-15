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
                settings.IncludeReasoningInModelContext,
                settings.MaxToolScreenshotsInModelContext);
            return FindSafeCutoffPoint(messages, rawCutoff);
        }

        var rawCutoffIndex = settings.KeepTokens > 0
            ? FindTokenBasedCutoff(
                messages,
                estimatedTokens,
                settings.KeepTokens,
                settings.IncludeReasoningInModelContext,
                settings.MaxToolScreenshotsInModelContext)
            : FindMessageBasedCutoff(messages, settings.KeepMessages);

        return FindSafeCutoffPoint(messages, rawCutoffIndex);
    }

    public static int DetermineTruncateArgsCutoff(
        IReadOnlyList<ChatMessage> messages,
        TruncateArgsSettings settings,
        bool includeReasoningInModelContext = false,
        int maxToolScreenshots = int.MaxValue)
    {
        int cutoff;
        if (settings.KeepTokens > 0)
        {
            cutoff = DetermineTruncateArgsCutoffFromKeepBudget(
                messages,
                settings.KeepTokens,
                includeReasoningInModelContext,
                maxToolScreenshots);
        }
        else
        {
            cutoff = Math.Max(0, messages.Count - settings.KeepMessages);
        }

        return FindSafeCutoffPoint(messages, cutoff);
    }

    public static int DetermineTruncateArgsCutoffFromKeepBudget(
        IReadOnlyList<ChatMessage> messages,
        int keepTokenBudget,
        bool includeReasoningInModelContext = false,
        int maxToolScreenshots = int.MaxValue)
    {
        if (keepTokenBudget <= 0 || messages.Count == 0)
        {
            return messages.Count;
        }

        var remainingToolScreenshots = Math.Max(0, maxToolScreenshots);
        var tokensKept = 0;
        var rawCutoff = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var messageTokens = ContextTokenEstimator.EstimateMessage(
                messages[index],
                includeReasoningInModelContext,
                ref remainingToolScreenshots);
            if (tokensKept + messageTokens > keepTokenBudget)
            {
                rawCutoff = index + 1;
                break;
            }

            tokensKept += messageTokens;
        }

        return FindSafeCutoffPoint(messages, rawCutoff);
    }

    /// <summary>
    /// True when every assistant tool_call in <c>[0, index)</c> has a matching tool result
    /// in that same prefix (open-set empty). Aligns with DSH <c>toolPairingBalancedBefore</c>.
    /// </summary>
    public static bool IsPairingBalancedBefore(IReadOnlyList<ChatMessage> messages, int index) =>
        IsPairingBalancedInRange(messages, 0, index);

    /// <summary>
    /// True when the span <c>[start, end)</c> is self-contained: every tool_call it opens is also
    /// closed inside it. Used to prove a retained window cannot leave an open <c>tool_call</c>.
    /// </summary>
    public static bool IsPairingBalancedInRange(
        IReadOnlyList<ChatMessage> messages,
        int start,
        int end)
    {
        if (start >= end || end <= 0)
        {
            return true;
        }

        var open = new HashSet<string>(StringComparer.Ordinal);
        var from = Math.Max(0, start);
        var limit = Math.Min(end, messages.Count);
        for (var i = from; i < limit; i++)
        {
            ApplyPairing(messages[i], open);
        }

        return open.Count == 0;
    }

    /// <summary>
    /// True when a retained tail may begin at <paramref name="start"/>.
    ///
    /// <para>A tail must never open with a tool result: the assistant <c>tool_call</c> it answers
    /// would sit in the summarized span, leaving an orphaned <c>tool_result</c> that the API
    /// rejects. Note this is not covered by <see cref="IsPairingBalancedInRange"/>, which only
    /// tracks unclosed calls — removing an unknown id is a no-op there.</para>
    /// </summary>
    public static bool IsRetainedTailStartSafe(IReadOnlyList<ChatMessage> messages, int start)
    {
        // Retaining from the beginning keeps everything, so nothing can be orphaned.
        if (start <= 0 || start >= messages.Count)
        {
            return true;
        }

        return messages[start].Role != MessageRole.Tool;
    }

    /// <summary>
    /// Walks <paramref name="cutoffIndex"/> back until the kept tail starts on a pairing-balanced
    /// boundary. Returns 0 when no earlier split is safe (caller should skip compact).
    /// </summary>
    public static int FindSafeCutoffPoint(IReadOnlyList<ChatMessage> messages, int cutoffIndex)
    {
        if (cutoffIndex <= 0)
        {
            return 0;
        }

        if (cutoffIndex >= messages.Count)
        {
            return cutoffIndex;
        }

        var lastBalanced = FindLastBalancedPrefixEnd(messages, cutoffIndex);
        return IsPairingBalancedBefore(messages, cutoffIndex) ? cutoffIndex : lastBalanced;
    }

    /// <summary>
    /// Smallest <c>end &gt;= minEnd</c> whose prefix <c>[0, end)</c> is pairing-balanced, or
    /// <see cref="IReadOnlyList{T}.Count"/> when no such boundary exists. Used where a retained
    /// count is a floor rather than a ceiling: growing it by a message or two keeps a tool pair
    /// whole, whereas trimming back could discard the window entirely.
    /// </summary>
    public static int FindNextBalancedPrefixEnd(IReadOnlyList<ChatMessage> messages, int minEnd)
    {
        var open = new HashSet<string>(StringComparer.Ordinal);
        var start = Math.Max(0, Math.Min(minEnd, messages.Count));
        for (var i = 0; i < start; i++)
        {
            ApplyPairing(messages[i], open);
        }

        if (open.Count == 0)
        {
            return start;
        }

        for (var i = start; i < messages.Count; i++)
        {
            ApplyPairing(messages[i], open);
            if (open.Count == 0)
            {
                return i + 1;
            }
        }

        return messages.Count;
    }

    /// <summary>Largest <c>end &lt;= limit</c> whose prefix <c>[0, end)</c> is pairing-balanced.</summary>
    private static int FindLastBalancedPrefixEnd(IReadOnlyList<ChatMessage> messages, int limit)
    {
        var open = new HashSet<string>(StringComparer.Ordinal);
        var lastBalanced = 0;
        var bound = Math.Min(limit, messages.Count);
        for (var i = 0; i < bound; i++)
        {
            ApplyPairing(messages[i], open);
            if (open.Count == 0)
            {
                lastBalanced = i + 1;
            }
        }

        return lastBalanced;
    }

    private static void ApplyPairing(ChatMessage message, HashSet<string> open)
    {
        if (message.Role == MessageRole.Assistant)
        {
            var calls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
            if (calls is { Count: > 0 })
            {
                foreach (var call in calls)
                {
                    if (!string.IsNullOrWhiteSpace(call.Id))
                    {
                        open.Add(call.Id);
                    }
                }
            }

            return;
        }

        if (message.Role != MessageRole.Tool)
        {
            return;
        }

        var toolCallId = ModelMessageBuilder.ExtractToolCallId(message.Content);
        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            open.Remove(toolCallId);
        }
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
        bool includeReasoningInModelContext,
        int maxToolScreenshots)
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
            if (ContextTokenEstimator.EstimateSuffix(
                    messages,
                    mid,
                    includeReasoningInModelContext,
                    maxToolScreenshots) <= keepTokens)
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
