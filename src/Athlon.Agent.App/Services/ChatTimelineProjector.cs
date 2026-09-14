using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Projects a transcript into per-turn segments for AG-UI replay.
///
/// A turn is projected into an ordered list of <see cref="TurnBlock"/>s so the timeline can
/// interleave the activity fold with content bubbles exactly as they were produced: assistant
/// replies, tool cards and per-edit cards keep their transcript order, and the reasoning / activity
/// tools that surround them collapse into an <see cref="ActivityBlock"/> placed at that point.
/// Folding a whole turn into a single activity card (plus one "final" reply) would force a fixed
/// ordering that a fully rebuilt timeline cannot reproduce identically.
///
/// The <c>publish_plan</c> call is deliberately absent from every list: it renders as a plan card
/// that the plan store owns (see <see cref="PlanTimelinePolicy"/>), so its turn only exposes
/// <see cref="TurnSegment.HasPlanPublish"/> for the caller to place that card.
/// </summary>
internal static class ChatTimelineProjector
{
    /// <summary>An ordered piece of a turn: either an activity fold or a content bubble.</summary>
    internal abstract record TurnBlock;

    /// <summary>Reasoning and activity tools collapsed into one fold at this point in the turn.</summary>
    internal sealed record ActivityBlock(
        IReadOnlyList<ChatMessageViewModel> Messages) : TurnBlock;

    /// <summary>A single content bubble: an assistant reply, tool card, or per-edit card.</summary>
    internal sealed record ContentBlock(
        ChatMessageViewModel Message) : TurnBlock;

    internal sealed record TurnSegment(
        IReadOnlyList<ChatMessageViewModel> UserMessages,
        /// <summary>Activity folds and content bubbles, in transcript order.</summary>
        IReadOnlyList<TurnBlock> Blocks,
        ChatMessageViewModel? CompactionMessage,
        DateTimeOffset? TurnUserCreatedAt,
        /// <summary>
        /// Message id of the user/compaction turn boundary this segment belongs to. Stable across
        /// restarts, so it is the anchor used for the turn's activity/files entry ids.
        /// </summary>
        string? TurnAnchorId,
        /// <summary>
        /// Index this segment occupies in the replayed timeline, exposed so the caller can compute
        /// seq values with <see cref="TimelineOrderPolicy"/> for entries it appends after the
        /// projection (currently the plan-ready card).
        /// </summary>
        long TurnIndex,
        /// <summary>True when the turn called <c>publish_plan</c>; see <see cref="PlanTimelinePolicy"/>.</summary>
        bool HasPlanPublish);

    public static IReadOnlyList<TurnSegment> BuildSegments(
        IReadOnlyList<ChatMessageViewModel> timeline,
        bool showToolCalls)
    {
        var segments = new List<TurnSegment>();
        var blocks = new List<TurnBlock>();
        var activityBlock = new List<ChatMessageViewModel>();
        DateTimeOffset? turnUserCreatedAt = null;
        string? turnAnchorId = null;
        var hasPlanPublish = false;

        // An activity fold only exists once it has content; an empty accumulator is dropped so a
        // turn that went straight to a reply does not gain a phantom fold.
        void FlushActivityBlock()
        {
            if (activityBlock.Count == 0)
            {
                return;
            }

            blocks.Add(new ActivityBlock(activityBlock.ToArray()));
            activityBlock.Clear();
        }

        void FlushTurn()
        {
            FlushActivityBlock();
            if (blocks.Count > 0 || hasPlanPublish)
            {
                segments.Add(new TurnSegment(
                    UserMessages: Array.Empty<ChatMessageViewModel>(),
                    Blocks: blocks.ToArray(),
                    CompactionMessage: null,
                    TurnUserCreatedAt: turnUserCreatedAt,
                    TurnAnchorId: turnAnchorId,
                    TurnIndex: segments.Count,
                    HasPlanPublish: hasPlanPublish));
            }

            blocks.Clear();
            hasPlanPublish = false;
        }

        foreach (var message in timeline)
        {
            if (message.IsHiddenPlaceholder)
            {
                continue;
            }

            if (message.IsUser)
            {
                FlushTurn();
                turnUserCreatedAt = message.CreatedAtUtc;
                turnAnchorId = message.MessageId;
                segments.Add(new TurnSegment(
                    UserMessages: [message],
                    Blocks: Array.Empty<TurnBlock>(),
                    CompactionMessage: null,
                    TurnUserCreatedAt: turnUserCreatedAt,
                    TurnAnchorId: turnAnchorId,
                    TurnIndex: segments.Count,
                    HasPlanPublish: false));
                continue;
            }

            if (message.IsCompaction)
            {
                if (ChatDisplayPolicy.ShouldDisplayCompactionCheckpoint(message))
                {
                    FlushTurn();
                    turnUserCreatedAt = null;
                    turnAnchorId = message.MessageId;
                    segments.Add(new TurnSegment(
                        UserMessages: Array.Empty<ChatMessageViewModel>(),
                        Blocks: Array.Empty<TurnBlock>(),
                        CompactionMessage: message,
                        TurnUserCreatedAt: null,
                        TurnAnchorId: turnAnchorId,
                        TurnIndex: segments.Count,
                        HasPlanPublish: false));
                }

                continue;
            }

            if (message.IsTool)
            {
                if (PlanTimelinePolicy.IsPublishPlanTool(message.ToolName))
                {
                    // A dedicated plan-ready card renders this call; it is never a tool card and
                    // never activity (folding it would hide the turn's publish slot).
                    hasPlanPublish = true;
                    continue;
                }

                // A successful edit renders as its own single-file card at the edit's slot in the
                // turn's content stream — not folded into the activity summary and not aggregated
                // into one per-turn "N files changed" card. Placing it here is what makes file
                // modifications appear along the timeline in the order they happened.
                if (IsSucceededFileEdit(message) || ShouldEmitToolCard(showToolCalls, message))
                {
                    FlushActivityBlock();
                    blocks.Add(new ContentBlock(message));
                    continue;
                }

                if (TurnActivityClassifier.IsActivityTool(message.ToolName))
                {
                    activityBlock.Add(message);
                }

                continue;
            }

            if (message.HasReasoning)
            {
                activityBlock.Add(new ChatMessageViewModel(
                    ChatMessage.Create(
                        MessageRole.Assistant,
                        string.Empty,
                        reasoningContent: message.ReasoningContent)));
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                // Every non-empty assistant reply is its own bubble; the activity that surrounded it
                // stays in the fold(s) on either side of it.
                FlushActivityBlock();
                blocks.Add(new ContentBlock(message));
            }
        }

        FlushTurn();
        return segments;
    }

    internal static bool ShouldEmitToolCard(
        bool showToolCalls,
        ChatMessageViewModel message)
    {
        // A pending approval must always surface a card, otherwise hiding tool cards would swallow
        // the approve/deny buttons and strand the turn. ChatDisplayPolicy encodes that exception.
        if (!showToolCalls && message.ToolApprovalState != ToolApprovalState.Pending)
        {
            return false;
        }

        return ChatDisplayPolicy.ShouldIncludeToolViewModel(showToolCalls, message);
    }

    /// <summary>
    /// A file tool that finished successfully. These are rendered one card per edit (see
    /// <see cref="IsSucceededFileEdit"/>) instead of folding into the turn's file-change summary.
    /// </summary>
    internal static bool IsSucceededFileEdit(ChatMessageViewModel message) =>
        message.IsTool
        && ModifiedFilePathExtractor.IsFileTool(message.ToolName)
        && message.ToolApprovalState is not (ToolApprovalState.Pending or ToolApprovalState.Denied)
        && ModifiedFilePathExtractor.ToModifiedFileStatus(message.ToolCallStatus) == ModifiedFileStatus.Succeeded;
}
