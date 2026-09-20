using System.Collections.Specialized;
using System.Windows.Threading;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Incremental chat-view sync and streaming dispatch: pushing single-item updates into the
/// web view, flushing streamed tokens, and dispatching stream events. Split out of the
/// orchestration file.
/// </summary>
public sealed partial class SessionTurnUiController
{
    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!CanSyncChatView || _bulkChatViewSyncDepth > 0)
        {
            return;
        }

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                _modifiedFilesTracker.RebuildFromMessages(Messages);
                SyncChatView();
                break;
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems?.Count == 1 && e.NewItems[0] is ChatMessageViewModel single)
                {
                    if (single.IsUser)
                    {
                        DispatchUserMessageToChatView(single);
                    }
                    else if (single.IsCompaction)
                    {
                        // Only visible (manual) checkpoints split the fold. Hidden auto-compaction
                        // must not seal, or replay+live upsert stacks two activity cards.
                        if (ChatDisplayPolicy.ShouldDisplayCompactionCheckpoint(single))
                        {
                            SealActivitySegment();
                            // A compaction checkpoint owns the turn boundary from here on.
                            _currentTurnAnchorId = single.MessageId;
                            _activityBlockIndex = 0;
                            if (CanTouchChatView)
                            {
                                _ = ChatView!.ApplyToolResultMarkdownAsync(single);
                            }
                        }
                    }
                    else if (!IsStreamingChatItem(single))
                    {
                        if (HasLiveTurnSurface)
                        {
                            DispatchIncrementalChatItem(single);
                        }
                        else
                        {
                            SyncChatView();
                        }
                    }
                }
                else if (!HasLiveTurnSurface)
                {
                    SyncChatView();
                }

                break;
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (!HasLiveTurnSurface)
                {
                    SyncChatView();
                }

                break;
        }
    }

    /// <summary>
    /// The turn still owns UI that a full replay would duplicate (live FILES_CHANGED paths, a live
    /// activity fold, streaming bubbles). Callers must not re-implement a narrower version — a full
    /// reload while a turn still has live paths re-emits a card from replay, and the live upsert
    /// then creates a second card that repeats those files.
    /// </summary>
    private bool HasLiveTurnSurface =>
        _modifiedFilesTracker.HasCurrentTurnPaths
        || _turnActivityTracker.HasSegmentContent
        || _streaming.ActiveAssistantBubble is not null
        || _streaming.ToolBubblesByIndex.Count > 0;

    private void DispatchIncrementalChatItem(ChatMessageViewModel message)
    {
        if (!CanTouchChatView)
        {
            return;
        }

        if (message.IsTool || message.IsCompaction)
        {
            _ = ChatView!.ApplyToolResultMarkdownAsync(message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            // Duration is attached only when sealing the final turn reply.
            _ = ChatView!.ApplyAssistantMarkdownAsync(message);
        }
    }

    private static bool IsStreamingChatItem(ChatMessageViewModel message) =>
        message.IsStreaming || message.StreamToolIndex is not null;

    private void FinalizeStreamingDisplay()
    {
        if (!IsDisplayed || ChatView is null)
        {
            return;
        }

        var lastUserIndex = -1;
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].IsUser)
            {
                lastUserIndex = i;
                break;
            }
        }

        ChatMessageViewModel? lastAssistant = null;
        for (var i = lastUserIndex + 1; i < Messages.Count; i++)
        {
            var message = Messages[i];
            if (message.IsHiddenPlaceholder)
            {
                continue;
            }

            if (message.IsTool || message.IsCompaction)
            {
                var detail = !string.IsNullOrWhiteSpace(message.ToolDetailExpandedDisplay)
                    ? message.ToolDetailExpandedDisplay
                    : !string.IsNullOrWhiteSpace(message.ToolDetail)
                        ? message.ToolDetail
                        : message.ToolSummary;
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    _ = ChatView.ApplyToolResultMarkdownAsync(message);
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                if (lastAssistant is not null)
                {
                    _ = ChatView.ApplyAssistantMarkdownAsync(lastAssistant, streaming: false);
                }

                lastAssistant = message;
            }
        }

        if (lastAssistant is not null)
        {
            _ = ChatView.ApplyAssistantMarkdownAsync(
                lastAssistant,
                streaming: false,
                ResolveTurnResponseDurationMs(lastAssistant));
        }
    }

    private int? ResolveTurnResponseDurationMs(ChatMessageViewModel assistant)
    {
        ChatMessageViewModel? turnUser = null;
        foreach (var message in Messages)
        {
            if (message.IsUser)
            {
                turnUser = message;
                continue;
            }

            if (ReferenceEquals(message, assistant)
                || string.Equals(message.MessageId, assistant.MessageId, StringComparison.Ordinal))
            {
                break;
            }
        }

        return turnUser is null
            ? null
            : ChatEventSerializer.ComputeResponseDurationMs(turnUser.CreatedAtUtc, assistant.CreatedAtUtc);
    }

    private void SyncChatView(bool immediate = false, bool authoritative = false)
    {
        if (!CanSyncChatView)
        {
            return;
        }

        if (immediate)
        {
            Interlocked.Increment(ref _syncChatViewGeneration);
            _ = ReloadChatViewAsync(authoritative);
            return;
        }

        var generation = Interlocked.Increment(ref _syncChatViewGeneration);
        _dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (generation != _syncChatViewGeneration || !CanSyncChatView)
            {
                return;
            }

            _ = ReloadChatViewAsync(authoritative);
        });
    }

    private void TrimMessagesIfNeeded()
    {
        TrimViewModelCacheIfNeeded();
    }

    private void TrimViewModelCacheIfNeeded()
    {
        if (_viewModelCache.Count <= MaxViewModelCacheSize)
        {
            return;
        }

        var liveIds = new HashSet<string>(
            Messages.Select(message => message.MessageId),
            StringComparer.Ordinal);
        foreach (var key in _viewModelCache.Keys.Where(key => !liveIds.Contains(key)).Take(_viewModelCache.Count - MaxViewModelCacheSize))
        {
            _viewModelCache.Remove(key);
        }
    }

    private void FlushBufferedStreamingToUi()
    {
        foreach (var streamEvent in _tokenBuffer.DrainPendingStreamEvents())
        {
            ProcessUiStreamEvents(streamEvent, notifyTracker: false);
        }

        FlushStreamingTokens();
    }

    private void NotifyChatViewAfterStreamEvent(AgentStreamEvent streamEvent)
    {
        if (!IsDisplayed || ChatView is null)
        {
            return;
        }

        if (streamEvent is AgentStreamEvent.TextMessageEnd(var endMessageId))
        {
            var assistant = Messages.LastOrDefault(message =>
                string.Equals(message.MessageId, endMessageId, StringComparison.Ordinal));
            if (assistant is not null && !string.IsNullOrWhiteSpace(assistant.Content))
            {
                // The reply is its own bubble; seal the fold that preceded it so the next activity
                // starts a fresh card after this bubble.
                SealActivitySegment();
                _ = ChatView.ApplyAssistantMarkdownAsync(assistant, streaming: false);
            }

            return;
        }

        if (streamEvent is AgentStreamEvent.ToolCallResult(var toolCallId, _, _))
        {
            var toolMessage = Messages.LastOrDefault(message =>
                message.IsTool
                && string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal));
            if (toolMessage is not null)
            {
                _ = ChatView.ApplyToolResultMarkdownAsync(toolMessage);
            }
        }
    }

    private void FlushStreamingTokens()
    {
        var (pendingTokens, pendingReasoning, textMessageId, reasoningMessageId) = _tokenBuffer.PeekPending();
        _tokenBuffer.FlushTokens(Messages, IsDisplayed, RequestScroll);
        if (!IsDisplayed || ChatView is null)
        {
            return;
        }

        // Reasoning is folded into TURN_ACTIVITY; do not emit standalone purple thought bubbles.
        if (pendingReasoning.Length > 0 && reasoningMessageId is not null)
        {
            _turnActivityTracker.Process(
                new AgentStreamEvent.ReasoningMessageContent(reasoningMessageId, pendingReasoning));
            PublishTurnActivity();
        }

        if (pendingTokens.Length > 0 && textMessageId is not null)
        {
            // Live-render Markdown from the accumulated assistant content (C# Markdig → HTML).
            var assistant = FindAssistantMessage(textMessageId);
            if (assistant is not null && !string.IsNullOrWhiteSpace(assistant.Content))
            {
                _ = ChatView.ApplyAssistantMarkdownAsync(assistant, streaming: true);
            }
        }
    }

    private ChatMessageViewModel? FindAssistantMessage(string messageId) =>
        Messages.LastOrDefault(message =>
            !message.IsUser
            && !message.IsTool
            && string.Equals(message.MessageId, messageId, StringComparison.Ordinal));

    private void DispatchToChatView(AgentStreamEvent streamEvent)
    {
        if (!IsDisplayed || ChatView is null || !ShouldDispatchToChatView(streamEvent))
        {
            return;
        }

        _ = ChatView.DispatchEventAsync(streamEvent);
    }

    private void DispatchUserMessageToChatView(ChatMessageViewModel message)
    {
        if (!IsDisplayed || ChatView is null)
        {
            return;
        }

        _ = ChatView.DispatchUserMessageAsync(message);
    }

    private bool ShouldDispatchToChatView(AgentStreamEvent streamEvent)
    {
        return streamEvent is not AgentStreamEvent.UsageRecorded
            and not AgentStreamEvent.ContextHygieneApplied
            and not AgentStreamEvent.ContextBudgetUpdated
            and not AgentStreamEvent.ChatMessageAppended
            and not AgentStreamEvent.ClearEmptyAssistantPlaceholder
            and not AgentStreamEvent.ReasoningMessageStart
            and not AgentStreamEvent.ReasoningMessageContent
            and not AgentStreamEvent.ReasoningMessageEnd
        && !TurnActivityClassifier.IsActivityToolStreamEvent(streamEvent, _turnActivityTracker.ResolveToolName)
        && (_showToolCalls() || !ChatDisplayPolicy.IsToolStreamEvent(streamEvent));
    }

    private ChatMessageViewModel? FindToolMessage(string? toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return null;
        }

        return Messages.LastOrDefault(message =>
            message.IsTool && string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal));
    }

    private IReadOnlyList<AgentToolCall> CollectIncompleteToolCalls(AgentSession session)
    {
        var answered = ChatTimelineHydrator.BuildAnsweredToolCallIds(session.Messages);
        var incomplete = new Dictionary<string, AgentToolCall>(StringComparer.Ordinal);

        foreach (var message in Messages)
        {
            if (!message.IsTool || string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                continue;
            }

            if (answered.Contains(message.ToolCallId) || incomplete.ContainsKey(message.ToolCallId))
            {
                continue;
            }

            if (message.ToolCallStatus is ToolCallDisplayStatus.Preparing
                or ToolCallDisplayStatus.Running
                or ToolCallDisplayStatus.Cancelled)
            {
                incomplete[message.ToolCallId] = new AgentToolCall(
                    message.ToolCallId,
                    string.IsNullOrWhiteSpace(message.ToolName) ? "unknown" : message.ToolName,
                    new Dictionary<string, string>());
            }
        }

        return incomplete.Values.ToList();
    }
}
