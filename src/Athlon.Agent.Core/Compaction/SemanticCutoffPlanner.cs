using System.Text;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Core.Compaction;

public static class SemanticCutoffPlanner
{
    public static int DetermineCutoffIndex(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        int keepTokenBudget) =>
        DetermineCutPlan(conversation, settings, keepTokenBudget).SummarizedEnd;

    /// <summary>
    /// Resolves the compaction cut as a head/tail split.
    ///
    /// <para>The cut is the more conservative (earlier) of two bounds: the semantic protected-tail
    /// start and the token-budget tail start. The protected tail is anchored at the last real user
    /// message but capped by <see cref="ContextCompactionSettings.ProtectedTailMaxMessages"/>, so a
    /// long agentic turn can summarize its own earlier tool output instead of pinning the cutoff
    /// behind the user message that opened the turn.</para>
    ///
    /// <para>When the cap moves the cut past that user message, the message is reported via
    /// <see cref="ConversationCutPlan.UserAnchorIndex"/> so the caller can re-attach it verbatim
    /// after the summary.</para>
    /// </summary>
    public static ConversationCutPlan DetermineCutPlan(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        int keepTokenBudget)
    {
        if (conversation.Count == 0 || keepTokenBudget <= 0)
        {
            return new ConversationCutPlan(0, conversation.Count, null);
        }

        var anchorIndex = FindProtectedTailStart(conversation);
        var protectedStart = ApplyProtectedTailCap(conversation, anchorIndex, settings);
        var tokenKeepStart = FindTokenBasedTailStart(
            conversation,
            keepTokenBudget,
            settings.IncludeReasoningInModelContext,
            settings.MaxToolScreenshotsInModelContext);
        var rawCutoff = Math.Min(protectedStart, tokenKeepStart);

        // One balanced-boundary resolution covers both sides: a cutoff whose prefix has no open
        // tool_call also guarantees the retained tail cannot start on an orphaned tool result.
        var summarizedEnd = ConversationCutoffPlanner.FindSafeCutoffPoint(conversation, rawCutoff);
        summarizedEnd = SnapToRetainedTailBoundary(conversation, summarizedEnd);
        var userAnchor = ResolveUserAnchorIndex(conversation, settings, anchorIndex, summarizedEnd);

        return new ConversationCutPlan(summarizedEnd, summarizedEnd, userAnchor);
    }

    /// <summary>
    /// Moves a cutoff that would open the retained tail on a tool result forward to the next
    /// assistant turn. The prefix balance check does not catch this case, because dropping a
    /// <c>tool_result</c> whose <c>tool_call</c> stayed in the prefix looks like an unmatched removal.
    /// </summary>
    private static int SnapToRetainedTailBoundary(IReadOnlyList<ChatMessage> conversation, int cutoff)
    {
        if (ConversationCutoffPlanner.IsRetainedTailStartSafe(conversation, cutoff))
        {
            return cutoff;
        }

        var index = cutoff;
        while (index < conversation.Count && conversation[index].Role == MessageRole.Tool)
        {
            index++;
        }

        // No safe boundary ahead inside the conversation: refuse to compact this time.
        return index >= conversation.Count ? 0 : index;
    }

    /// <summary>
    /// Wraps an already resolved cutoff (manual compaction, forced fallback, non-dynamic mode) into a
    /// <see cref="ConversationCutPlan"/>.
    ///
    /// <para>Deliberately does not re-attach the user message: these paths keep their historical
    /// behaviour where the user instruction is protected by <c>must_preserve</c> only. Re-attachment
    /// exists for the semantic path, where the protected-tail cap moved the cut past the message
    /// that opened the current turn.</para>
    /// </summary>
    public static ConversationCutPlan CreatePlanForCutoff(
        IReadOnlyList<ChatMessage> conversation,
        int cutoff)
    {
        var summarizedEnd = ConversationCutoffPlanner.FindSafeCutoffPoint(conversation, cutoff);
        summarizedEnd = SnapToRetainedTailBoundary(conversation, summarizedEnd);
        return summarizedEnd <= 0
            ? new ConversationCutPlan(0, conversation.Count, null)
            : new ConversationCutPlan(summarizedEnd, summarizedEnd, null);
    }

    public static string? BuildMustPreserveAppendix(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        int keepTokenBudget)
    {
        if (!settings.DynamicCompaction.EnableSemanticCutoff || conversation.Count == 0)
        {
            return null;
        }

        var cutoff = DetermineCutoffIndex(conversation, settings, keepTokenBudget);
        if (cutoff <= 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("<must_preserve>");
        builder.AppendLine("The following facts from earlier history MUST appear in your summary:");

        for (var index = 0; index < cutoff; index++)
        {
            var message = conversation[index];
            if (!SemanticMessageScorer.ShouldPreserveInSummary(message))
            {
                continue;
            }

            builder.AppendLine($"- [{message.Role}] {TruncateForAppendix(message.Content)}");
        }

        builder.AppendLine("</must_preserve>");
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Index of the last real user message, or <see cref="IReadOnlyList{T}.Count"/> when the
    /// conversation has none. Summary placeholders and synthetic control messages are skipped so
    /// machine-generated turns cannot pin the cutoff.
    /// </summary>
    private static int FindProtectedTailStart(IReadOnlyList<ChatMessage> conversation)
    {
        for (var index = conversation.Count - 1; index >= 0; index--)
        {
            var message = conversation[index];
            if (message.Role != MessageRole.User)
            {
                continue;
            }

            if (SummaryMessageBuilder.IsSummaryMessage(message) || IsHiddenControlMessage(message))
            {
                continue;
            }

            return index;
        }

        return conversation.Count;
    }

    /// <summary>
    /// Caps how far back the protected tail may reach. Without this, the tail anchored at a turn's
    /// opening user message grows with every tool round and makes the whole turn unsummarizable.
    /// </summary>
    private static int ApplyProtectedTailCap(
        IReadOnlyList<ChatMessage> conversation,
        int anchorIndex,
        ContextCompactionSettings settings)
    {
        var cap = settings.ProtectedTailMaxMessages;
        if (cap < 1 || anchorIndex >= conversation.Count)
        {
            return anchorIndex;
        }

        // A larger start index protects fewer messages, so max() only ever lets the cap shorten
        // the protected tail — never extend it.
        var capStart = Math.Max(0, conversation.Count - cap);
        return Math.Max(anchorIndex, capStart);
    }

    /// <summary>
    /// Returns the user-message index to re-attach when the cut advanced past it; otherwise null.
    /// </summary>
    private static int? ResolveUserAnchorIndex(
        IReadOnlyList<ChatMessage> conversation,
        ContextCompactionSettings settings,
        int anchorIndex,
        int summarizedEnd)
    {
        if (!settings.ReattachLatestUserMessage
            || anchorIndex >= conversation.Count
            || anchorIndex >= summarizedEnd)
        {
            return null;
        }

        return anchorIndex;
    }

    /// <summary>
    /// Synthetic user messages that drive automation (auto-continue, approved plan, background
    /// sub-agent wake-ups) carry a marker. They are control messages rather than user intent, so
    /// they must not anchor the protected tail or be re-attached as the active instruction.
    /// </summary>
    private static bool IsHiddenControlMessage(ChatMessage message) =>
        PlanContinuePrompt.IsPlanContinueMessage(message)
        || SubAgentAutoContinuePrompt.IsAutoContinueMessage(message)
        || ApprovedPlanPrompt.IsApprovedPlanMessage(message);

    private static int FindTokenBasedTailStart(
        IReadOnlyList<ChatMessage> conversation,
        int keepTokenBudget,
        bool includeReasoningInModelContext,
        int maxToolScreenshots)
    {
        var remainingToolScreenshots = Math.Max(0, maxToolScreenshots);
        var tokensKept = 0;
        for (var index = conversation.Count - 1; index >= 0; index--)
        {
            tokensKept += ContextTokenEstimator.EstimateMessage(
                conversation[index],
                includeReasoningInModelContext,
                ref remainingToolScreenshots);
            if (tokensKept > keepTokenBudget)
            {
                return Math.Min(conversation.Count, index + 1);
            }
        }

        return 0;
    }

    private static string TruncateForAppendix(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "(empty)";
        }

        var normalized = content.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "...";
    }
}
