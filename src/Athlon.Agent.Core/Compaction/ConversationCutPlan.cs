namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// How one compaction pass splits a conversation.
///
/// <para>Two shapes are possible. A short conversation keeps the historical layout: everything
/// before <see cref="SummarizedEnd"/> becomes the summary and the rest is retained verbatim. A long
/// agentic turn can push the cutoff past the last real user message; in that case
/// <see cref="UserAnchorIndex"/> records where that message was so the caller can re-attach it after
/// the summary instead of losing the active instruction to summarization.</para>
/// </summary>
/// <param name="SummarizedEnd">
/// Exclusive end of the span folded into the summary. Messages in <c>[0, SummarizedEnd)</c> are
/// replaced by one summary placeholder.
/// </param>
/// <param name="RetainedTailStart">
/// Inclusive start of the span kept verbatim. Messages in <c>[RetainedTailStart, Count)</c> are
/// preserved in order.
/// </param>
/// <param name="UserAnchorIndex">
/// Index of the last real user message when it falls inside the summarized span (so it can be
/// re-attached), or <c>null</c> when no re-attachment is needed.
/// </param>
public sealed record ConversationCutPlan(
    int SummarizedEnd,
    int RetainedTailStart,
    int? UserAnchorIndex)
{
    /// <summary>True when this plan summarizes nothing and must not trigger a compaction pass.</summary>
    public bool IsEmpty => SummarizedEnd <= 0;

    /// <summary>
    /// True when the cutoff advanced past the last real user message and that message must be
    /// re-attached after the summary.
    /// </summary>
    public bool NeedsUserReattach => UserAnchorIndex is not null;
}
