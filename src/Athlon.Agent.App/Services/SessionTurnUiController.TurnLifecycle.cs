using System.Windows.Threading;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Live-turn lifecycle: end snapshots, turn finalization, publishing activity/edit cards
/// and streamed UI event processing. Split out of the orchestration file.
/// </summary>
public sealed partial class SessionTurnUiController
{
    public SessionTurnEndSnapshot CaptureEndSnapshot(
        AgentSession session,
        bool wasCancelled,
        bool timedOut,
        string? errorMessage)
    {
        SessionTurnEndSnapshot? snapshot = null;
        RunOnUiSync(() =>
        {
            FlushBufferedStreamingToUi();
            FlushStreamingTokens();

            var (pendingTokens, pendingReasoning, _, _) = _tokenBuffer.PeekPending();

            var assistantContent = _streaming.ActiveAssistantBubble?.Content;
            if (pendingTokens.Length > 0)
            {
                assistantContent = (assistantContent ?? string.Empty) + pendingTokens;
            }

            var assistantReasoning = _streaming.ActiveAssistantBubble?.ReasoningContent;
            if (pendingReasoning.Length > 0)
            {
                assistantReasoning = (assistantReasoning ?? string.Empty) + pendingReasoning;
            }

            snapshot = new SessionTurnEndSnapshot(
                string.IsNullOrWhiteSpace(assistantContent) ? null : assistantContent,
                string.IsNullOrWhiteSpace(assistantReasoning) ? null : assistantReasoning,
                CollectIncompleteToolCalls(session),
                wasCancelled,
                timedOut,
                errorMessage);
        });

        return snapshot!;
    }

    public void FinalizeTurn(
        AgentSession session,
        IReadOnlyList<ChatMessage> persistedTurnMessages,
        bool cancelled,
        bool timedOut,
        int turnTimeoutMinutes,
        string? errorMessage = null)
    {
        RunOnUiSync(() =>
        {
            _tokenBuffer.StopFlushTimer();
            FlushBufferedStreamingToUi();
            FlushStreamingTokens();
            _tokenBuffer.ClearBuffers();

            if (cancelled)
            {
                if (_streaming.ActiveAssistantBubble is { } bubble)
                {
                    bubble.MarkStreamingCancelled();
                }

                foreach (var message in Messages.Where(static message => message.IsToolRunning))
                {
                    message.MarkToolCancelled();
                }

                foreach (var message in _streaming.ToolBubblesByIndex.Values.ToList())
                {
                    message.MarkStreamingToolCancelled();
                }
            }
            else if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                _streaming.Process(new AgentStreamEvent.ClearEmptyAssistantPlaceholder(), Messages);
                foreach (var message in _streaming.ToolBubblesByIndex.Values.ToList())
                {
                    message.MarkStreamingToolCancelled();
                }
            }

            // Any assistant text that streamed since the last tool boundary is already a bubble; the
            // remaining fold (reasoning / trailing tools) is sealed here. No text is folded.
            _streaming.Reset();
            ReconcilePendingToolsFromSession(session);
            MergeActivitySourceFromSession(session);
            _bulkChatViewSyncDepth++;
            try
            {
                ApplyPersistedTurnMessages(persistedTurnMessages, timedOut, turnTimeoutMinutes, errorMessage);
            }
            finally
            {
                _bulkChatViewSyncDepth--;
                FinalizeStreamingDisplay();
                // Seal the now-finished segment: this drops the provisional live reasoning and
                // reduces the fold to the transcript's own record.
                SealActivitySegment();
                // Re-render from the transcript so the resting timeline is byte-for-byte what a
                // session switch or restart would produce. The incremental live events only need to
                // look right while the turn is in flight; this replay is what settles the timeline
                // into the canonical projection.
                SyncChatView(immediate: true, authoritative: true);
                // Overlay the still-open fold (activity between the last seal and turn end) onto the
                // replayed fold, keyed by the same turn anchor and block index.
                RestoreLiveTurnCardsAfterReload();

                if (IsDisplayed)
                {
                    RequestScrollImmediate();
                }
            }
        });
    }

    /// <summary>
    /// Seals the activity fold currently being accumulated and starts a fresh one. Called when an
    /// assistant bubble closes a segment, at a compaction boundary, and at turn end. Edits are keyed
    /// by tool call id, so they are untouched here and keep their own slots.
    /// </summary>
    private void SealActivitySegment()
    {
        if (!CanTouchChatView)
        {
            // Keep the live segment so switching back can restore one fold with thought.
            // Wiping here drops reasoning and makes the next upsert a second, shorter card.
            _turnActivityTracker.FinishPendingThought();
            return;
        }

        _turnActivityTracker.FinishPendingThought();
        var summary = _turnActivityTracker.Snapshot();
        if (summary is { HasContent: true })
        {
            _ = ChatView!.DispatchTurnActivityAsync(
                summary,
                upsert: false,
                turnAnchorId: _currentTurnAnchorId,
                activityBlockIndex: _activityBlockIndex);
            // The block index tracks emitted activity folds, matching the replay's numbering, so it
            // only advances when a fold actually exists.
            _activityBlockIndex++;
        }

        _turnActivityTracker.BeginSegment();
    }

    private void PublishTurnActivity(bool upsert = true)
    {
        if (!CanTouchChatView)
        {
            return;
        }

        var summary = _turnActivityTracker.Snapshot();
        if (summary is null || !summary.HasContent)
        {
            return;
        }

        _ = ChatView!.DispatchTurnActivityAsync(
            summary,
            upsert: upsert,
            turnAnchorId: _currentTurnAnchorId,
            activityBlockIndex: _activityBlockIndex);
    }

    /// <summary>
    /// Publishes each completed file edit as its own timeline card. The card is keyed by the tool
    /// call id (matching replay's entry id), so re-publishing after a rebuild rewrites the same
    /// entry instead of stacking a twin.
    /// </summary>
    private void PublishEditCards()
    {
        if (!CanTouchChatView)
        {
            return;
        }

        foreach (var card in _modifiedFilesTracker.PeekSegmentEditCards())
        {
            _ = ChatView!.DispatchEditFileCardAsync(card.ToolCallId, card.Files);
        }
    }

    private void ProcessUiStreamEvents(AgentStreamEvent streamEvent, bool notifyTracker)
    {
        // An assistant bubble or a tool card closes the fold that preceded it. Sealing here keeps
        // the fold and the content in transcript order: an activity tool that arrives after a reply
        // must start a new fold rather than merging back into the one before the reply.
        SealOpenActivityFoldBeforeContent(streamEvent);

        if (notifyTracker)
        {
            _modifiedFilesTracker.Process(streamEvent);
            _turnActivityTracker.Process(streamEvent);
            TryAppendActivitySourceFromStreamEvent(streamEvent);
        }

        foreach (var uiEvent in _displayCoordinator.MapForUi(streamEvent))
        {
            DispatchToChatView(uiEvent);
            _streaming.Process(uiEvent, Messages);
            NotifyChatViewAfterStreamEvent(uiEvent);
        }

        if (streamEvent is AgentStreamEvent.ToolCallResult(var resultCallId, _, _)
            && !string.IsNullOrWhiteSpace(resultCallId)
            && _modifiedFilesTracker.PeekSegmentEditCards().Any(card =>
                string.Equals(card.ToolCallId, resultCallId, StringComparison.Ordinal)))
        {
            // A succeeded file edit becomes its own card, which the projector places after the fold.
            // Seal the fold before publishing the card so the live card lands after it too.
            SealOpenActivityFoldBeforeContent(opensContent: true);
        }

        if (streamEvent is AgentStreamEvent.ToolCallStart
            or AgentStreamEvent.ToolCallArgs
            or AgentStreamEvent.ToolCallEnd
            or AgentStreamEvent.ToolCallResult
            or AgentStreamEvent.ReasoningMessageContent
            or AgentStreamEvent.ReasoningMessageEnd)
        {
            PublishTurnActivity(upsert: true);
            if (streamEvent is AgentStreamEvent.ToolCallResult)
            {
                PublishEditCards();
            }
        }
    }

    /// <summary>
    /// Seals the open fold when a content-producing event is about to be rendered above it. Activity
    /// tools and reasoning keep accumulating; text and non-activity tool cards close the fold so they
    /// land after it in the shared seq stream.
    /// </summary>
    private void SealOpenActivityFoldBeforeContent(
        AgentStreamEvent? streamEvent = null,
        bool opensContent = false)
    {
        opensContent = opensContent || streamEvent switch
        {
            AgentStreamEvent.TextMessageStart => true,
            // A non-activity tool renders a card only when tool cards are shown; otherwise the
            // projector drops it, so the live fold must not split either.
            AgentStreamEvent.ToolCallStart(_, var toolName, _) =>
                _showToolCalls() && !TurnActivityClassifier.IsActivityTool(toolName),
            _ => false
        };

        if (opensContent && _turnActivityTracker.HasSegmentContent)
        {
            SealActivitySegment();
        }
    }

    private async Task DispatcherYieldAsync()
    {
        if (!_dispatcher.CheckAccess())
        {
            await Task.Yield();
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, () => tcs.TrySetResult());
        await tcs.Task.ConfigureAwait(true);
    }
}
