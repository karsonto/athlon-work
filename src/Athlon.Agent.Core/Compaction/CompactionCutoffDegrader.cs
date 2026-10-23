namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Overflow/Critical fallback when a pairing-safe cutoff cannot be found. Never leaves orphan
/// tool_calls without matching tool messages.
/// </summary>
public static class CompactionCutoffDegrader
{
    public const string OmittedToolContentPrefix = "[tool_result omitted for compaction]";

    public static bool ShouldDegrade(ContextCompactionSettings settings, ContextPressureLevel pressure, bool force) =>
        force
        || (pressure == ContextPressureLevel.Overflow && settings.EnableCutoffDegradeOnOverflow)
        || (pressure == ContextPressureLevel.Critical && settings.EnableCutoffDegradeOnCritical);

    /// <summary>
    /// Attempts to produce a usable cut plan when the normal planner returned cutoff 0.
    /// Strategy: force a tail of at most <see cref="ContextCompactionSettings.OverflowProtectedTailMaxMessages"/>
    /// messages, snap forward past tool_results, then shorten any tool bodies remaining in the tail.
    /// Returns null when degradation is impossible.
    /// </summary>
    public static (ConversationCutPlan Plan, IReadOnlyList<ChatMessage> Conversation)? TryDegrade(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings)
    {
        if (conversation.Count <= 1)
        {
            return null;
        }

        var keepTail = settings.OverflowProtectedTailMaxMessages > 0
            ? settings.OverflowProtectedTailMaxMessages
            : Math.Min(12, settings.ProtectedTailMaxMessages > 0 ? settings.ProtectedTailMaxMessages : 12);
        keepTail = Math.Max(1, Math.Min(keepTail, conversation.Count - 1));

        var rawCutoff = conversation.Count - keepTail;
        var plan = SemanticCutoffPlanner.CreatePlanForCutoff(conversation, rawCutoff);
        if (plan.SummarizedEnd <= 0)
        {
            // Force cutoff and rewrite the retained tail so it cannot start on orphan tool_results:
            // include trailing tool_results in the prefix until a non-tool message starts the tail.
            var forced = rawCutoff;
            while (forced < conversation.Count && conversation[forced].Role == MessageRole.Tool)
            {
                forced++;
            }

            if (forced >= conversation.Count || forced <= 0)
            {
                return null;
            }

            plan = new ConversationCutPlan(forced, forced, null, null);
        }

        var rewritten = ShortenTailToolBodies(conversation, plan.SummarizedEnd);
        return (plan, rewritten);
    }

    /// <summary>
    /// Keeps tool_call ids intact but replaces tool body text with a one-line omitted marker so the
    /// retained tail stays small without breaking pairing.
    /// </summary>
    public static IReadOnlyList<ChatMessage> ShortenTailToolBodies(
        IReadOnlyList<ChatMessage> conversation,
        int summarizedEnd)
    {
        if (summarizedEnd <= 0 || summarizedEnd >= conversation.Count)
        {
            return conversation;
        }

        var updated = new List<ChatMessage>(conversation.Count);
        for (var i = 0; i < conversation.Count; i++)
        {
            var message = conversation[i];
            if (i >= summarizedEnd
                && message.Role == MessageRole.Tool
                && !string.IsNullOrEmpty(message.Content)
                && !message.Content.StartsWith(OmittedToolContentPrefix, StringComparison.Ordinal))
            {
                var toolCallId = ModelMessageBuilder.ExtractToolCallId(message.Content);
                var shortened = string.IsNullOrWhiteSpace(toolCallId)
                    ? OmittedToolContentPrefix
                    : $"{OmittedToolContentPrefix} tool_call_id={toolCallId}";
                // Preserve the wire format ModelMessageBuilder expects when possible.
                if (message.Content.Contains("tool_call_id", StringComparison.OrdinalIgnoreCase)
                    || message.Content.StartsWith("{", StringComparison.Ordinal))
                {
                    shortened = message.Content.Length <= 120
                        ? message.Content
                        : OmittedToolContentPrefix + "\n" + message.Content[..Math.Min(80, message.Content.Length)];
                }

                message = message with { Content = shortened };
            }

            updated.Add(message);
        }

        return updated;
    }
}
