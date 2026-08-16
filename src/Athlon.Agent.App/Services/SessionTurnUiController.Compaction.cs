using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed partial class SessionTurnUiController
{
    private void AppendCompactionNotice(ChatMessage message) =>
        _streaming.Process(new Athlon.Agent.Core.Streaming.AgentStreamEvent.ChatMessageAppended(message), Messages);

    public void BeginManualCompactionBubble()
    {
        RunOnUiSync(() =>
        {
            RemovePendingManualCompactionBubbleCore();
            _bulkChatViewSyncDepth++;
            ChatMessageViewModel bubble;
            try
            {
                bubble = ChatMessageViewModel.CreatePendingManualCompaction();
                Messages.Add(bubble);
                TrimMessagesIfNeeded();
            }
            finally
            {
                _bulkChatViewSyncDepth--;
            }

            DispatchPendingCompactionBubbleStart(bubble);
            RequestScrollImmediate();
        });
    }

    public void DismissManualCompactionBubble()
    {
        RunOnUiSync(() =>
        {
            if (FindPendingManualCompactionBubble() is null)
            {
                return;
            }

            RemovePendingManualCompactionBubbleCore();
            SyncChatView(immediate: true);
            RequestScrollImmediate();
        });
    }

    public void CancelManualCompactionBubble()
    {
        RunOnUiSync(() =>
        {
            var pending = FindPendingManualCompactionBubble();
            if (pending is null)
            {
                return;
            }

            pending.MarkCompactionCancelled();
            DispatchPendingCompactionBubbleUpdate(pending);
            _bulkChatViewSyncDepth++;
            try
            {
                Messages.Remove(pending);
            }
            finally
            {
                _bulkChatViewSyncDepth--;
            }

            RequestScrollImmediate();
        });
    }

    public void CompleteManualCompactionBubble(
        ChatMessage auditMessage,
        IReadOnlyList<ChatMessage> compactedSessionMessages)
    {
        RunOnUiSync(() =>
        {
            var pending = FindPendingManualCompactionBubble();
            if (pending is null || auditMessage.Role != MessageRole.Compaction)
            {
                return;
            }

            pending.ApplyCompletedCompaction(auditMessage);
            _activitySourceMessages = compactedSessionMessages.ToList();
            DispatchPendingCompactionBubbleUpdate(pending);
            RequestScrollImmediate();
        });
    }

    private void DispatchPendingCompactionBubbleStart(ChatMessageViewModel bubble)
    {
        if (!IsDisplayed || ChatView is null)
        {
            return;
        }

        _ = ChatView.ApplyToolResultMarkdownAsync(bubble);
    }

    private void DispatchPendingCompactionBubbleUpdate(ChatMessageViewModel bubble)
    {
        if (!IsDisplayed || ChatView is null)
        {
            return;
        }

        _ = ChatView.ApplyToolResultMarkdownAsync(bubble);
    }

    private void RemovePendingManualCompactionBubbleCore()
    {
        var pending = FindPendingManualCompactionBubble();
        if (pending is null)
        {
            return;
        }

        _bulkChatViewSyncDepth++;
        try
        {
            Messages.Remove(pending);
        }
        finally
        {
            _bulkChatViewSyncDepth--;
        }
    }

    private ChatMessageViewModel? FindPendingManualCompactionBubble() =>
        Messages.LastOrDefault(message =>
            message.IsCompaction
            && message.IsToolRunning
            && string.Equals(
                message.MessageId,
                ChatMessageViewModel.PendingManualCompactionMessageId,
                StringComparison.Ordinal));
}
