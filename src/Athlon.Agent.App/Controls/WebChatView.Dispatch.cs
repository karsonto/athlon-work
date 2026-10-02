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
        PostTimelineEvent(ChatEventSerializer.SerializeStaticAssistantHtml(
            message,
            streaming,
            responseDurationMs,
            browserScreenshots: browserScreenshots));
        return Task.CompletedTask;
    }

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
        string? turnAnchorId = null)
    {
        // Empty upsert: nothing to show. Empty seal must still reach JS so a live card
        // is finalized and cannot be stolen by the next turn.
        if (files.Count == 0 && upsert)
        {
            return Task.CompletedTask;
        }

        PostTimelineEvent(ChatEventSerializer.SerializeFilesChanged(files, upsert, turnAnchorId: turnAnchorId));
        return Task.CompletedTask;
    }

    public Task DispatchEditFileCardAsync(string toolCallId, IReadOnlyList<ModifiedFileViewModel> files)
    {
        if (files.Count == 0 || string.IsNullOrWhiteSpace(toolCallId))
        {
            return Task.CompletedTask;
        }

        // Keyed by the tool call id so a live publish and its replayed twin share one entry.
        PostTimelineEvent(ChatEventSerializer.SerializeFilesChanged(
            files,
            upsert: true,
            entryId: "files:edit:" + toolCallId));
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
