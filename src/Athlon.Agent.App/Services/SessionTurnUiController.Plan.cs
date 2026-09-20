using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Plan-ready card publication and plan-run lookups. Split out of the orchestration file.
/// </summary>
public sealed partial class SessionTurnUiController
{
    /// <summary>
    /// Publishes (or refreshes) the plan-ready card for <paramref name="run"/>. Called by the plan
    /// bar when a run reaches AwaitConfirm/Done. The controller keeps the run so it survives a
    /// mid-session replay: the timeline is rebuilt from the transcript, and a plan run is not
    /// derived from the transcript, so nothing else would re-emit the card.
    /// </summary>
    public void ShowPlanReady(PlanRun run)
    {
        _visiblePlanRun = run;
        RunOnUiSync(() =>
        {
            var seq = PlanSeqFor(run);
            PlanTimelineEventObserver?.Invoke(ChatEventSerializer.SerializePlanReady(run, seq));
            if (IsDisplayed && ChatView is not null)
            {
                _ = ChatView.ShowPlanReadyAsync(run, seq);
            }
        });
    }

    /// <summary>Drops the plan-ready card (plan consumed by Build, or draft abandoned).</summary>
    public void ClearPlanReady()
    {
        var previous = _visiblePlanRun;
        _visiblePlanRun = null;
        if (previous is null)
        {
            return;
        }

        RunOnUiSync(() =>
        {
            PlanTimelineEventObserver?.Invoke(ChatEventSerializer.SerializePlanCleared(previous.Id));
            if (IsDisplayed && ChatView is not null)
            {
                _ = ChatView.ClearPlanReadyAsync(previous.Id);
            }
        });
    }

    /// <summary>
    /// The seq the plan card belongs at: the <see cref="TimelineOrderPolicy.Plan"/> slot of the
    /// turn whose <c>publish_plan</c> call produced the run. Returns <c>null</c> when that turn is
    /// not in the displayed window, in which case the card keeps the seq it already has.
    /// </summary>
    private long? PlanSeqFor(PlanRun run)
    {
        _ = run;
        var turnIndex = FindPlanPublishTurnIndex();
        return turnIndex is { } index ? TimelineOrderPolicy.Plan(index) : null;
    }

    /// <summary>
    /// Index of the turn that called <c>publish_plan</c>, by counting turn boundaries in the
    /// replay source the same way <see cref="ChatEventSerializer"/> does. Uses the newest such
    /// call, which is the run the plan bar just published.
    /// </summary>
    private long? FindPlanPublishTurnIndex()
    {
        var projected = ProjectActivitySourceViewModels(BuildReplayActivitySource());
        if (projected.Count == 0)
        {
            return null;
        }

        var segments = ChatTimelineProjector.BuildSegments(projected, _showToolCalls());
        long turnIndex = -1;
        long? result = null;
        foreach (var segment in segments)
        {
            // Mirrors ReplayTurnSegment numbering: the projector emits one leading segment per turn
            // (user message, compaction checkpoint, or a mid-turn tail page), so each projection
            // step here advances the turn index exactly once.
            turnIndex++;
            if (segment.HasPlanPublish)
            {
                result = turnIndex;
            }
        }

        return result;
    }

    private bool ShouldRefreshDisplayAfterSessionReplace(AgentSession session)
    {
        // Model-driven compaction (ConversationCompact / ForceCompact / MiddleCutOnRetrySkipped)
        // must not collapse the timeline. Its checkpoint audit arrives incrementally through
        // ChatMessageAppended, so the already-displayed history stays untouched and scrolling
        // back through it keeps working. Only manual compaction, which the user explicitly asked
        // for, replaces the display with the compacted session.
        foreach (var message in session.Messages)
        {
            if (message.Role != MessageRole.Compaction
                || CompactionAuditDisplay.Parse(message.Content).Strategy != CompactionStrategy.ManualCompact)
            {
                continue;
            }

            lock (_manualCompactionRefreshGate)
            {
                if (!_appliedManualCompactionAuditIds.Add(message.Id))
                {
                    continue;
                }
            }

            return true;
        }

        return false;
    }
}
