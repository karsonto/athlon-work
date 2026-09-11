using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Projects a transcript into per-turn segments for AG-UI replay.
///
/// A turn's content bubbles (tool cards and assistant replies) are kept in one
/// <see cref="TurnSegment.ContentMessages"/> list in transcript order so the timeline can number
/// them consecutively. Splitting them into separate tool/assistant lists and re-emitting tools
/// first — as this projector used to — made a turn where the model interleaves text and tools
/// render in a different order on replay than it did while streaming.
///
/// The <c>publish_plan</c> call is deliberately absent from every list: it renders as a plan card
/// that the plan store owns (see <see cref="PlanTimelinePolicy"/>), so its turn only exposes
/// <see cref="TurnSegment.HasPlanPublish"/> for the caller to place that card.
/// </summary>
internal static class ChatTimelineProjector
{
    internal sealed record TurnSegment(
        IReadOnlyList<ChatMessageViewModel> UserMessages,
        IReadOnlyList<ChatMessageViewModel> ActivitySegment,
        /// <summary>Tool cards and assistant replies, in transcript order.</summary>
        IReadOnlyList<ChatMessageViewModel> ContentMessages,
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
        var activitySegment = new List<ChatMessageViewModel>();
        var contentMessages = new List<ChatMessageViewModel>();
        var finalAssistantMessageIds = FindFinalAssistantMessageIds(timeline);
        DateTimeOffset? turnUserCreatedAt = null;
        string? turnAnchorId = null;
        var hasPlanPublish = false;

        void FlushTurnIntermediate()
        {
            if (activitySegment.Count > 0 || contentMessages.Count > 0 || hasPlanPublish)
            {
                segments.Add(new TurnSegment(
                    UserMessages: Array.Empty<ChatMessageViewModel>(),
                    ActivitySegment: activitySegment.ToArray(),
                    ContentMessages: contentMessages.ToArray(),
                    CompactionMessage: null,
                    TurnUserCreatedAt: turnUserCreatedAt,
                    TurnAnchorId: turnAnchorId,
                    TurnIndex: segments.Count,
                    HasPlanPublish: hasPlanPublish));
            }

            activitySegment.Clear();
            contentMessages.Clear();
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
                FlushTurnIntermediate();
                turnUserCreatedAt = message.CreatedAtUtc;
                turnAnchorId = message.MessageId;
                segments.Add(new TurnSegment(
                    UserMessages: [message],
                    ActivitySegment: Array.Empty<ChatMessageViewModel>(),
                    ContentMessages: Array.Empty<ChatMessageViewModel>(),
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
                    FlushTurnIntermediate();
                    turnUserCreatedAt = null;
                    turnAnchorId = message.MessageId;
                    segments.Add(new TurnSegment(
                        UserMessages: Array.Empty<ChatMessageViewModel>(),
                        ActivitySegment: Array.Empty<ChatMessageViewModel>(),
                        ContentMessages: Array.Empty<ChatMessageViewModel>(),
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
                    // never activity (folding it would hide the turn's final assistant reply).
                    hasPlanPublish = true;
                    continue;
                }

                // A successful edit renders as its own single-file card at the edit's slot in the
                // turn's content stream — not folded into the activity summary and not aggregated
                // into one per-turn "N files changed" card. Placing it here is what makes file
                // modifications appear along the timeline in the order they happened.
                if (IsSucceededFileEdit(message))
                {
                    contentMessages.Add(message);
                    continue;
                }

                if (ShouldEmitToolCard(showToolCalls, message))
                {
                    contentMessages.Add(message);
                    continue;
                }

                if (TurnActivityClassifier.IsActivityTool(message.ToolName))
                {
                    activitySegment.Add(message);
                }

                continue;
            }

            if (message.HasReasoning)
            {
                activitySegment.Add(new ChatMessageViewModel(
                    ChatMessage.Create(
                        MessageRole.Assistant,
                        string.Empty,
                        reasoningContent: message.ReasoningContent)));
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                if (finalAssistantMessageIds.Contains(message.MessageId))
                {
                    contentMessages.Add(message);
                }
                else
                {
                    activitySegment.Add(message);
                }
            }
        }

        FlushTurnIntermediate();
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

    internal static HashSet<string> FindFinalAssistantMessageIds(
        IReadOnlyList<ChatMessageViewModel> timeline)
    {
        var finals = new HashSet<string>(StringComparer.Ordinal);
        var turnHasActivity = false;
        var turnAssistantIds = new List<string>();

        void CloseTurn()
        {
            if (turnAssistantIds.Count > 0)
            {
                if (turnHasActivity)
                {
                    finals.Add(turnAssistantIds[^1]);
                }
                else
                {
                    foreach (var id in turnAssistantIds)
                    {
                        finals.Add(id);
                    }
                }
            }

            turnHasActivity = false;
            turnAssistantIds.Clear();
        }

        foreach (var message in timeline)
        {
            if (message.IsHiddenPlaceholder)
            {
                continue;
            }

            if (message.IsUser || message.IsCompaction)
            {
                CloseTurn();
                continue;
            }

            if (message.IsTool)
            {
                if (!PlanTimelinePolicy.IsPublishPlanTool(message.ToolName)
                    && TurnActivityClassifier.IsActivityTool(message.ToolName))
                {
                    turnHasActivity = true;
                }
            }
            else if (message.HasReasoning)
            {
                turnHasActivity = true;
            }

            if (!message.IsTool && !string.IsNullOrWhiteSpace(message.Content))
            {
                turnAssistantIds.Add(message.MessageId);
            }
        }

        CloseTurn();
        return finals;
    }
}
