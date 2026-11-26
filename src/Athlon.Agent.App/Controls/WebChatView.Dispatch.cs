using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.Chat;
using Athlon.Agent.App.Services.Diagnostics;
using Athlon.Agent.App.Services.Streaming;
using Athlon.Agent.App.Themes;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Core.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Controls;

/// <summary>Public timeline dispatch: messages, tools, plans and approvals.</summary>
public partial class WebChatView
{
    public async Task LoadMessagesAsync(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls = false,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null,
        Athlon.Agent.Core.Plan.PlanRun? planRun = null,
        string? sessionId = null,
        ChatReplaySnapshotCache? replayCache = null)
    {
        // Snapshot immediately rather than holding the live per-session collection.
        // Concurrent hydration can otherwise mutate the collection mid-render (e.g. a
        // session switch) and produce a half-populated array (only the last user message).
        _pendingMessages = messages.ToArray();
        _pendingShowToolCalls = showToolCalls;
        _pendingActivitySourceMessages = activitySourceMessages?.ToArray();
        _pendingPlanRun = planRun;
        _pendingSessionId = sessionId;
        _pendingReplayCache = replayCache;
        _needsRender = true;
        var generation = StartRenderGeneration();
        // Logged with the message count and the live viewport/size state. The decisive combination is
        // a request that arrives while CanRender() is false: that is the collision between "a switch
        // clears the collection, shrinking the WebView to 2x2" and "a render was asked for right
        // then", and it is the moment an orphan gets created.
        ChatRenderTrace.Record(
            "request",
            $"gen={generation} msgs={_pendingMessages.Count} session={sessionId ?? "-"} "
            + $"canRender={CanRender()} visible={IsVisible} w={ActualWidth:0.##} h={ActualHeight:0.##}");
        await RunRenderPipelineSafeAsync(generation).ConfigureAwait(true);

        if (_needsRender && generation == _renderGeneration)
        {
            ScheduleRenderRetry();
        }
    }

    public Task ApplyAssistantMarkdownAsync(
        ChatMessageViewModel message,
        bool streaming = false,
        int? responseDurationMs = null,
        IReadOnlyList<ImageAttachment>? browserScreenshots = null)
    {
        // Streaming display uses ASSISTANT_STREAM_TEXT (line blur-fade). Markdown HTML is reserved
        // for finalize / replay so we do not Markdig on every token flush.
        if (streaming)
        {
            return ApplyAssistantStreamTextAsync(message.MessageId, message.Content ?? string.Empty);
        }

        // Drop any queued stream-text follow-up; finalize owns the bubble from here.
        _queuedAssistantStreamText = null;

        var content = message.Content ?? string.Empty;
        var decision = _assistantMarkdownCoalesce.TryBegin(message.MessageId, content, streaming: false);
        switch (decision)
        {
            case CoalesceDecision.SkipUnchanged:
                return Task.CompletedTask;
            case CoalesceDecision.Queued:
                // Keep the live ViewModel reference so a follow-up flush sees the latest Content.
                _queuedAssistantMarkdown = new AssistantMarkdownQueueItem(
                    message,
                    Streaming: false,
                    responseDurationMs,
                    browserScreenshots);
                return Task.CompletedTask;
            default:
                _queuedAssistantMarkdown = null;
                return PostAssistantMarkdownCoalescedAsync(
                    message,
                    streaming: false,
                    responseDurationMs,
                    browserScreenshots);
        }
    }

    public Task ApplyAssistantStreamTextAsync(string messageId, string text)
    {
        var decision = _assistantStreamTextCoalesce.TryBegin(messageId, text ?? string.Empty, streaming: true);
        switch (decision)
        {
            case CoalesceDecision.SkipUnchanged:
                return Task.CompletedTask;
            case CoalesceDecision.Queued:
                _queuedAssistantStreamText = new AssistantStreamTextQueueItem(messageId, text ?? string.Empty);
                return Task.CompletedTask;
            default:
                _queuedAssistantStreamText = null;
                return PostAssistantStreamTextCoalescedAsync(messageId, text ?? string.Empty);
        }
    }

    private async Task PostAssistantStreamTextCoalescedAsync(string messageId, string text)
    {
        try
        {
            var json = ChatEventSerializer.SerializeAssistantStreamText(messageId, text);
            await PostTimelineEventAsync(json).ConfigureAwait(true);
        }
        catch
        {
            _assistantStreamTextCoalesce.Fail();
            _queuedAssistantStreamText = null;
            throw;
        }

        _assistantStreamTextCoalesce.Complete();
        var queued = _queuedAssistantStreamText;
        _queuedAssistantStreamText = null;
        if (queued is null)
        {
            return;
        }

        var followDecision = _assistantStreamTextCoalesce.TryBegin(
            queued.MessageId,
            queued.Text,
            streaming: true);
        if (followDecision == CoalesceDecision.PostNow)
        {
            await PostAssistantStreamTextCoalescedAsync(queued.MessageId, queued.Text).ConfigureAwait(true);
        }
    }

    private async Task PostAssistantMarkdownCoalescedAsync(
        ChatMessageViewModel message,
        bool streaming,
        int? responseDurationMs,
        IReadOnlyList<ImageAttachment>? browserScreenshots)
    {
        try
        {
            var json = ChatEventSerializer.SerializeStaticAssistantHtml(
                message,
                streaming,
                responseDurationMs,
                browserScreenshots: browserScreenshots);
            await PostTimelineEventAsync(json).ConfigureAwait(true);
        }
        catch
        {
            _assistantMarkdownCoalesce.Fail();
            _queuedAssistantMarkdown = null;
            throw;
        }

        _assistantMarkdownCoalesce.Complete();
        var queued = _queuedAssistantMarkdown;
        _queuedAssistantMarkdown = null;
        if (queued is null)
        {
            return;
        }

        var followDecision = _assistantMarkdownCoalesce.TryBegin(
            queued.Message.MessageId,
            queued.Message.Content ?? string.Empty,
            queued.Streaming);
        if (followDecision == CoalesceDecision.PostNow)
        {
            await PostAssistantMarkdownCoalescedAsync(
                    queued.Message,
                    queued.Streaming,
                    queued.ResponseDurationMs,
                    queued.BrowserScreenshots)
                .ConfigureAwait(true);
        }
    }

    private sealed record AssistantMarkdownQueueItem(
        ChatMessageViewModel Message,
        bool Streaming,
        int? ResponseDurationMs,
        IReadOnlyList<ImageAttachment>? BrowserScreenshots);

    private sealed record AssistantStreamTextQueueItem(string MessageId, string Text);

    public Task ApplyToolResultMarkdownAsync(ChatMessageViewModel message)
    {
        PostTimelineEvent(ChatEventSerializer.SerializeToolResultMarkdown(message));
        return Task.CompletedTask;
    }

    public Task DispatchUserMessageAsync(ChatMessageViewModel message)
    {
        PostTimelineEvent(ChatEventSerializer.SerializeUserMessage(message));
        return Task.CompletedTask;
    }

    public Task DispatchFilesChangedAsync(
        IReadOnlyList<ModifiedFileViewModel> files,
        bool upsert = true,
        string? turnAnchorId = null,
        long? seq = null)
    {
        // Empty upsert: nothing to show. Empty seal must still reach JS so a live card
        // is finalized and cannot be stolen by the next turn.
        if (files.Count == 0 && upsert)
        {
            return Task.CompletedTask;
        }

        PostTimelineEvent(ChatEventSerializer.SerializeFilesChanged(
            files,
            upsert,
            seq,
            turnAnchorId: turnAnchorId));
        return Task.CompletedTask;
    }

    public Task DispatchTurnActivityAsync(
        TurnActivitySummary summary,
        bool upsert = true,
        string? turnAnchorId = null,
        int activityBlockIndex = 0)
    {
        if (!summary.HasContent)
        {
            return Task.CompletedTask;
        }

        PostTimelineEvent(ChatEventSerializer.SerializeTurnActivity(
            summary,
            upsert,
            turnAnchorId: turnAnchorId,
            activityBlockIndex: activityBlockIndex));
        return Task.CompletedTask;
    }

    public Task DispatchTurnActivityOutputAsync(string toolCallId, string delta)
    {
        if (string.IsNullOrEmpty(toolCallId) || string.IsNullOrEmpty(delta))
        {
            return Task.CompletedTask;
        }

        PostTimelineEvent(ChatEventSerializer.SerializeTurnActivityOutput(toolCallId, delta));
        return Task.CompletedTask;
    }

    public Task DispatchEventAsync(AgentStreamEvent streamEvent)
    {
        PostTimelineEvent(ChatEventSerializer.Serialize(streamEvent));
        return Task.CompletedTask;
    }

    public Task ShowToolApprovalAsync(PendingToolApproval approval, string arguments)
    {
        PostTimelineEvent(ChatEventSerializer.SerializeToolApprovalRequest(approval, arguments));
        return Task.CompletedTask;
    }

    public Task ResolveToolApprovalAsync(string toolCallId, ToolApprovalDecision decision)
    {
        PostTimelineEvent(ChatEventSerializer.SerializeToolApprovalResolved(toolCallId, decision));
        return Task.CompletedTask;
    }

    public Task ShowPlanReadyAsync(Athlon.Agent.Core.Plan.PlanRun run, long? seq = null)
    {
        PostTimelineEvent(ChatEventSerializer.SerializePlanReady(run, seq));
        return Task.CompletedTask;
    }

    /// <summary>Removes a plan-ready card the plan bar had published (plan abandoned or superseded).</summary>
    public Task ClearPlanReadyAsync(string runId)
    {
        PostTimelineEvent(ChatEventSerializer.SerializePlanCleared(runId));
        return Task.CompletedTask;
    }
}
