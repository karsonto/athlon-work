using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Codex-style checkpoint reshape: summarize everything before recent real user messages
/// (token budget from newest), then keep a pairing-safe open tool tail when needed.
/// </summary>
public static class CheckpointHistoryBuilder
{
    public sealed record CheckpointSplit(
        IReadOnlyList<ChatMessage> PrefixToSummarize,
        IReadOnlyList<ChatMessage> RetainedUsers,
        IReadOnlyList<ChatMessage> PairingTail,
        int SummarizedEndExclusive);

    public static int ResolveRetainUserTokenBudget(
        ContextCompactionSettings settings,
        ContextPressureLevel pressure,
        bool force)
    {
        if (force || pressure == ContextPressureLevel.Overflow)
        {
            return Math.Max(512, settings.OverflowRetainRecentUserMaxTokens);
        }

        return Math.Max(512, settings.RetainRecentUserMaxTokens);
    }

    /// <summary>
    /// Splits conversation into prefix (to summarize) and retained recent user messages + optional
    /// pairing tail for unclosed tool_calls at the end.
    /// </summary>
    public static CheckpointSplit? TrySplit(
        IReadOnlyList<ChatMessage> conversation,
        int retainUserTokenBudget)
    {
        if (conversation.Count == 0)
        {
            return null;
        }

        retainUserTokenBudget = Math.Max(0, retainUserTokenBudget);
        var selectedUserIndices = SelectRecentUserIndices(conversation, retainUserTokenBudget);
        if (selectedUserIndices.Count == 0)
        {
            // No real user messages: summarize all but keep a pairing-safe last slice if possible.
            if (conversation.Count <= 1)
            {
                return null;
            }

            var fallbackCutoff = ConversationCutoffPlanner.FindSafeCutoffPoint(
                conversation,
                Math.Max(1, conversation.Count - 1));
            if (fallbackCutoff <= 0)
            {
                fallbackCutoff = ForcePairingSafeCutoff(conversation, conversation.Count - 1);
            }

            if (fallbackCutoff <= 0 || fallbackCutoff >= conversation.Count)
            {
                return null;
            }

            return new CheckpointSplit(
                conversation.Take(fallbackCutoff).ToList(),
                Array.Empty<ChatMessage>(),
                conversation.Skip(fallbackCutoff).ToList(),
                fallbackCutoff);
        }

        var earliestUserIndex = selectedUserIndices.Min();
        var cutoff = ConversationCutoffPlanner.FindSafeCutoffPoint(conversation, earliestUserIndex);
        if (cutoff <= 0)
        {
            cutoff = ForcePairingSafeCutoff(conversation, earliestUserIndex);
        }

        if (cutoff <= 0)
        {
            return null;
        }

        // Users that fell before the pairing-adjusted cutoff are dropped into the prefix.
        var retainedUsers = selectedUserIndices
            .Where(i => i >= cutoff)
            .OrderBy(i => i)
            .Select(i => conversation[i])
            .ToList();

        var afterUsers = new List<ChatMessage>();
        var lastRetainedUserIndex = selectedUserIndices.Where(i => i >= cutoff).DefaultIfEmpty(-1).Max();
        for (var i = Math.Max(cutoff, lastRetainedUserIndex + 1); i < conversation.Count; i++)
        {
            afterUsers.Add(conversation[i]);
        }

        // If the conversation ends with an open tool group, keep that whole group even when it is
        // not a "user" message — otherwise the next API call 400s.
        var pairingTail = BuildPairingTail(conversation, cutoff, retainedUsers, afterUsers);

        var prefix = conversation.Take(cutoff).ToList();
        if (prefix.Count == 0)
        {
            return null;
        }

        return new CheckpointSplit(prefix, retainedUsers, pairingTail, cutoff);
    }

    private static List<int> SelectRecentUserIndices(
        IReadOnlyList<ChatMessage> conversation,
        int retainUserTokenBudget)
    {
        var indices = new List<int>();
        if (retainUserTokenBudget <= 0)
        {
            return indices;
        }

        var remaining = retainUserTokenBudget;
        for (var i = conversation.Count - 1; i >= 0; i--)
        {
            var message = conversation[i];
            if (!IsRetainableUserMessage(message))
            {
                continue;
            }

            var tokens = Math.Max(1, ContextTokenEstimator.EstimateMessage(message));
            if (tokens > remaining && indices.Count > 0)
            {
                break;
            }

            if (tokens > remaining)
            {
                // Single oversized user: keep a truncated copy via index; truncation applied later.
                indices.Add(i);
                break;
            }

            indices.Add(i);
            remaining -= tokens;
        }

        indices.Reverse();
        return indices;
    }

    public static bool IsRetainableUserMessage(ChatMessage message) =>
        message.Role == MessageRole.User
        && !SummaryMessageBuilder.IsSummaryMessage(message)
        && !SessionHandoffNote.IsHandoffMessage(message)
        && !ApprovedPlanPrompt.IsApprovedPlanMessage(message);

    /// <summary>
    /// Messages after retained users that must stay for tool pairing (assistant tool_calls + tools).
    /// </summary>
    private static List<ChatMessage> BuildPairingTail(
        IReadOnlyList<ChatMessage> conversation,
        int cutoff,
        IReadOnlyList<ChatMessage> retainedUsers,
        IReadOnlyList<ChatMessage> afterUsers)
    {
        if (afterUsers.Count == 0)
        {
            return [];
        }

        // Walk from end: if pairing is unbalanced in [cutoff, end), keep from the last safe start.
        if (ConversationCutoffPlanner.IsPairingBalancedInRange(conversation, cutoff, conversation.Count))
        {
            // Balanced: only keep afterUsers if they are not already represented as retained users.
            // Typically afterUsers are assistant/tool noise after the last user — drop them so the
            // checkpoint stays user-centric (Codex). Exception: empty retained users handled above.
            return [];
        }

        // Unbalanced: find the earliest index >= cutoff where the suffix to end is pairing-safe to start.
        var start = cutoff;
        for (var i = conversation.Count - 1; i >= cutoff; i--)
        {
            if (ConversationCutoffPlanner.IsRetainedTailStartSafe(conversation, i)
                && ConversationCutoffPlanner.IsPairingBalancedInRange(conversation, i, conversation.Count))
            {
                start = i;
                break;
            }
        }

        // Exclude messages already in retainedUsers (by index range overlapping user indices).
        var userIds = new HashSet<string>(retainedUsers.Select(m => m.Id), StringComparer.Ordinal);
        return conversation.Skip(start).Where(m => !userIds.Contains(m.Id)).ToList();
    }

    /// <summary>
    /// When FindSafeCutoffPoint returns 0, force a cut that does not leave the retained suffix
    /// starting on an orphan tool result.
    /// </summary>
    private static int ForcePairingSafeCutoff(IReadOnlyList<ChatMessage> conversation, int desiredCutoff)
    {
        desiredCutoff = Math.Clamp(desiredCutoff, 1, conversation.Count - 1);
        var cutoff = desiredCutoff;
        while (cutoff < conversation.Count && conversation[cutoff].Role == MessageRole.Tool)
        {
            cutoff++;
        }

        if (cutoff >= conversation.Count)
        {
            return 0;
        }

        if (!ConversationCutoffPlanner.IsPairingBalancedBefore(conversation, cutoff))
        {
            // Include trailing open assistants into the prefix until balanced.
            while (cutoff < conversation.Count
                   && !ConversationCutoffPlanner.IsPairingBalancedBefore(conversation, cutoff))
            {
                cutoff++;
            }
        }

        return cutoff < conversation.Count ? cutoff : 0;
    }
}
