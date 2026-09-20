using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Activity-source maintenance: keeping the transcript slice that replays TURN_ACTIVITY /
/// FILES_CHANGED in sync with the displayed window, and re-anchoring live turn cards. Split out of
/// the orchestration file so the "how activity is projected" code stays separate from turn
/// orchestration, streaming and approvals.
/// </summary>
public sealed partial class SessionTurnUiController
{
    private void TrimActivitySourceToDisplayedMessages()
    {
        if (_activitySourceMessages.Count == 0 || Messages.Count == 0)
        {
            return;
        }

        var firstUserId = Messages.FirstOrDefault(message => message.IsUser)?.MessageId;
        if (string.IsNullOrWhiteSpace(firstUserId))
        {
            return;
        }

        var startIndex = _activitySourceMessages.FindIndex(message =>
            string.Equals(message.Id, firstUserId, StringComparison.Ordinal));
        if (startIndex > 0)
        {
            _activitySourceMessages.RemoveRange(0, startIndex);
        }
    }

    private static List<ChatMessage> PrependDistinct(
        IReadOnlyList<ChatMessage> olderMessages,
        IReadOnlyList<ChatMessage> currentMessages)
    {
        var merged = new List<ChatMessage>(olderMessages.Count + currentMessages.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in olderMessages)
        {
            if (seen.Add(message.Id))
            {
                merged.Add(message);
            }
        }

        foreach (var message in currentMessages)
        {
            if (seen.Add(message.Id))
            {
                merged.Add(message);
            }
        }

        return merged;
    }

    private void AppendActivitySourceMessage(ChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Id))
        {
            return;
        }

        // The display page mirrors every durable append (including hidden summary placeholders,
        // which the display log also contains); the activity source below stays role-restricted.
        SyncDisplayModelWithAppend(message);

        if (message.Role is not (MessageRole.User or MessageRole.Tool or MessageRole.Assistant or MessageRole.Compaction))
        {
            return;
        }

        if (_activitySourceMessages.Any(existing =>
                string.Equals(existing.Id, message.Id, StringComparison.Ordinal)))
        {
            return;
        }

        _activitySourceMessages.Add(message);
    }

    /// <summary>
    /// Keeps the display-page model aligned with durable appends so loading an older page merges
    /// against the real window. Only tracks once a page has been hydrated; a brand-new session has
    /// no page model yet and reloads it from disk on the next switch.
    /// </summary>
    private void SyncDisplayModelWithAppend(ChatMessage message)
    {
        if (_displayMessages.Count == 0
            || _displayMessages.Any(existing => string.Equals(existing.Id, message.Id, StringComparison.Ordinal)))
        {
            return;
        }

        _displayMessages.Add(message);
    }

    private void TryAppendActivitySourceFromStreamEvent(AgentStreamEvent streamEvent)
    {
        switch (streamEvent)
        {
            case AgentStreamEvent.ToolCallResult(_, var content, var messageId):
                AppendActivitySourceMessage(ChatMessage.CreateWithId(messageId, MessageRole.Tool, content));
                break;
            case AgentStreamEvent.ChatMessageAppended(var message):
                AppendActivitySourceMessage(message);
                break;
        }
    }

    /// <summary>
    /// Re-points the live activity/files anchor at the transcript's own user message for the
    /// current turn.
    ///
    /// <see cref="AddUserMessage"/> anchors the live cards to the id of the user message the UI
    /// created, but the runtime persists a *different* user message id, and replay derives its
    /// anchor from that transcript id. A live upsert and its replayed twin therefore carry two
    /// different entry ids, and the web view draws them side by side at the same seq slot instead
    /// of collapsing them. Re-deriving the anchor from the authoritative source whenever the
    /// display is rebuilt from disk keeps both paths on one id.
    /// </summary>
    private void ReanchorLiveTurnToTranscript()
    {
        // Mid-turn the transcript's trailing user message is this turn's; a resting transcript
        // still names the turn that owns its tail, which FinalizeTurn's re-publish needs.
        var anchorId = _activitySourceMessages.LastOrDefault(
                static message => message.Role is MessageRole.User or MessageRole.Compaction)
            ?.Id
            ?? Messages.LastOrDefault(static message => message.IsUser)?.MessageId;
        if (!string.IsNullOrWhiteSpace(anchorId))
        {
            _currentTurnAnchorId = anchorId;
        }
    }

    private void RestoreLiveTurnCardsAfterReload()
    {
        if (_streaming.ActiveAssistantBubble is null
            && _streaming.ToolBubblesByIndex.Count == 0
            && !_turnActivityTracker.HasSegmentContent)
        {
            return;
        }

        // The replayed cards were keyed by the transcript's turn boundary; re-point the live
        // anchor at that same message before re-publishing so the upsert rewrites those cards in
        // place instead of stacking a twin beside them.
        ReanchorLiveTurnToTranscript();

        // Per-edit cards are keyed by tool call id in both paths, so re-publishing the segment's
        // edits after a reload rewrites the replayed cards in place rather than stacking twins.
        PublishEditCards();

        var live = _turnActivityTracker.Snapshot();
        if (live is not { HasContent: true } || !CanTouchChatView)
        {
            return;
        }

        // The still-open live fold is block N of the current turn. Overlay its live reasoning onto
        // whatever the replay projected for block N, so the upsert rewrites that same entry instead
        // of stacking a card that merges every fold of the turn.
        var replayed = TurnActivitySummaryBuilder.Build(CurrentReplayedActivityBlock(_activityBlockIndex));
        var summary = TurnActivitySummaryBuilder.OverlayLiveThought(replayed, live);
        if (summary.HasContent)
        {
            _ = ChatView!.DispatchTurnActivityAsync(
                summary,
                upsert: true,
                turnAnchorId: _currentTurnAnchorId,
                activityBlockIndex: _activityBlockIndex);
        }
    }

    /// <summary>
    /// Activity messages the replay projects for the current turn's block at <paramref name="blockIndex"/>.
    /// Empty when that fold does not exist in the transcript yet (it is being streamed live).
    /// </summary>
    private IReadOnlyList<ChatMessageViewModel> CurrentReplayedActivityBlock(int blockIndex)
    {
        if (string.IsNullOrWhiteSpace(_currentTurnAnchorId))
        {
            return Array.Empty<ChatMessageViewModel>();
        }

        var projected = ProjectActivitySourceViewModels(BuildReplayActivitySource());
        var segments = ChatTimelineProjector.BuildSegments(projected, _showToolCalls());

        // Anchor on the current turn's own segment. Scanning by anchor (rather than "last segment
        // with activity") keeps a turn whose first fold has not been persisted yet from borrowing a
        // previous turn's fold.
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(segments[i].TurnAnchorId, _currentTurnAnchorId, StringComparison.Ordinal))
            {
                continue;
            }

            var activityBlocks = segments[i].Blocks
                .OfType<ChatTimelineProjector.ActivityBlock>()
                .ToList();
            return blockIndex >= 0 && blockIndex < activityBlocks.Count
                ? activityBlocks[blockIndex].Messages
                : Array.Empty<ChatMessageViewModel>();
        }

        return Array.Empty<ChatMessageViewModel>();
    }

    /// <summary>
    /// Materializes the activity-fold view models for <paramref name="messages"/> with the same
    /// projection <see cref="ChatEventSerializer.BuildReplayEvents"/> uses, so turn boundaries
    /// counted here match the replay's turn indices exactly.
    /// </summary>
    internal static List<ChatMessageViewModel> ProjectActivitySourceViewModels(
        IReadOnlyList<ChatMessage> messages) =>
        messages
            .Where(message => message.Role is MessageRole.User
                or MessageRole.Tool
                or MessageRole.Assistant
                or MessageRole.Compaction)
            .Select(message => new ChatMessageViewModel(message))
            .ToList();

    private List<ChatMessage> BuildReplayActivitySource()
    {
        if (_activitySourceMessages.Count == 0)
        {
            return _activitySourceMessages;
        }

        // Slice from the turn-start (User/Compaction) that owns the FIRST displayed message.
        //
        // The display window can begin mid-turn (e.g. it is the tail page and starts with an
        // assistant/tool message, especially with preserveActiveTurn). Anchoring on the first
        // *User* in the window would then slice from the window's end and discard the activity
        // above it, so TURN_ACTIVITY / FILES_CHANGED replay goes missing. Anchoring on the first
        // displayed message and walking back to its owning turn keeps the replay complete.
        var firstDisplayedId = Messages
            .FirstOrDefault(message => !message.IsHiddenPlaceholder)
            ?.MessageId;
        if (string.IsNullOrWhiteSpace(firstDisplayedId))
        {
            return _activitySourceMessages;
        }

        var firstIndex = _activitySourceMessages.FindIndex(message =>
            string.Equals(message.Id, firstDisplayedId, StringComparison.Ordinal));
        if (firstIndex < 0)
        {
            // The first displayed message has no activity-source entry; keep the whole source
            // so its owning turn is not dropped.
            return _activitySourceMessages;
        }

        var startIndex = firstIndex;
        while (startIndex > 0
            && !ConversationActivitySource.StartsAtTurnBoundary(_activitySourceMessages[startIndex]))
        {
            startIndex--;
        }

        if (startIndex == 0)
        {
            return _activitySourceMessages;
        }

        return _activitySourceMessages.GetRange(startIndex, _activitySourceMessages.Count - startIndex);
    }

    private void MergeActivitySourceFromSession(AgentSession session)
    {
        if (session.Messages.Count == 0)
        {
            return;
        }

        int startIndex;
        if (_activitySourceMessages.Count > 0)
        {
            var lastId = _activitySourceMessages[^1].Id;
            var lastIndex = -1;
            for (var i = 0; i < session.Messages.Count; i++)
            {
                if (string.Equals(session.Messages[i].Id, lastId, StringComparison.Ordinal))
                {
                    lastIndex = i;
                    break;
                }
            }

            // Missing last id: keep the existing paged source instead of copying the full transcript.
            startIndex = lastIndex >= 0 ? lastIndex + 1 : session.Messages.Count;
        }
        else
        {
            startIndex = -1;
            var firstDisplayedId = Messages.FirstOrDefault(message =>
                !message.IsHiddenPlaceholder
                && (message.IsUser || message.IsTool || !string.IsNullOrWhiteSpace(message.Content)))
                ?.MessageId;
            if (!string.IsNullOrWhiteSpace(firstDisplayedId))
            {
                for (var i = 0; i < session.Messages.Count; i++)
                {
                    if (string.Equals(session.Messages[i].Id, firstDisplayedId, StringComparison.Ordinal))
                    {
                        startIndex = i;
                        break;
                    }
                }
            }

            if (startIndex < 0)
            {
                return;
            }
        }

        for (var i = startIndex; i < session.Messages.Count; i++)
        {
            AppendActivitySourceMessage(session.Messages[i]);
        }

        var backfill = ConversationActivitySource.CollectTurnStartBackfill(
            session.Messages,
            _activitySourceMessages);
        if (backfill.Count > 0)
        {
            _activitySourceMessages = ConversationActivitySource.PrependOlder(
                backfill,
                _activitySourceMessages);
        }
    }

    private bool ContainsMessageId(string messageId) =>
        !string.IsNullOrWhiteSpace(messageId)
        && Messages.Any(message => string.Equals(message.MessageId, messageId, StringComparison.Ordinal));

    private static bool ShouldHideMessageFromChat(ChatMessage message) =>
        ChatTimelineHydrator.ShouldHideMessageFromChat(message);
}
