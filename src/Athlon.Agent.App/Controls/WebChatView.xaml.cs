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

public partial class WebChatView : UserControl
{
    private readonly ChatHtmlBuilder _htmlBuilder = new();
    private Task? _initTask;
    private bool _initialized;
    private bool _documentReady;
    private TaskCompletionSource<bool> _documentReadyTcs = CreateCompletedDocumentReadyTcs();
    private int _navigationGeneration;
    private int _renderGeneration;
    private readonly SemaphoreSlim _renderOperationGate = new(1, 1);
    private readonly object _renderBarrierGate = new();
    private int _renderBarrierGeneration;
    private TaskCompletionSource<bool> _renderBarrier = CreateCompletedRenderBarrier();
    private IReadOnlyList<ChatMessageViewModel> _pendingMessages = Array.Empty<ChatMessageViewModel>();
    private bool _pendingShowToolCalls;
    private IReadOnlyList<ChatMessage>? _pendingActivitySourceMessages;
    private Athlon.Agent.Core.Plan.PlanRun? _pendingPlanRun;
    private string? _pendingSessionId;

    /// <summary>
    /// Pending reply to a Phase-4 <c>switchSession</c> probe: set while the timeline is asked to
    /// swap a session's rendered DOM back in, cleared when the page answers
    /// <c>snapshotRestored</c> / <c>snapshotMiss</c>. A timeout falls back to the normal replay,
    /// which stays authoritative.
    /// </summary>
    private TaskCompletionSource<bool>? _pendingSnapshotSwitch;

    private static readonly TimeSpan SnapshotSwitchTimeout = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How long a wanted render may stay unrendered before it is reported as dropped. Long enough
    /// that a normally-scheduled retry settles first, short enough to catch a stall within the same
    /// user action rather than minutes later.
    /// </summary>
    private static readonly TimeSpan OrphanWatchDelay = TimeSpan.FromMilliseconds(1500);
    private ChatReplaySnapshotCache? _pendingReplayCache;
    private bool _needsRender;
    private bool _renderRetryScheduled;
    private bool _renderInProgress;
    private bool _renderQueuedWhileInProgress;
    private int _themeApplyGeneration;
    private int _i18nApplyGeneration;

    public WebChatView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += OnSizeChanged;
    }

    public event EventHandler<string>? InitializationFailed;
    public event EventHandler<string>? ScriptExecutionFailed;
    public event EventHandler? OlderMessagesRequested;
    public event EventHandler<string>? ExternalLinkRequested;
    public event EventHandler<ToolApprovalDecisionEventArgs>? ToolApprovalDecisionReceived;
    public event EventHandler<ToolDetailRequestEventArgs>? ToolDetailRequested;
    public event EventHandler? PlanBuildRequested;
    public event EventHandler? PlanReviseRequested;

    /// <summary>
    /// Read-aloud orchestration, assigned by the window once services are resolved. Null leaves the
    /// timeline's play button inert (the button is also hidden when TTS is disabled).
    /// </summary>
    public ChatTtsController? TtsController { get; set; }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        AppCultureManager.CultureChanged -= OnAppCultureChanged;
        // Leaving the chat page must silence any in-flight utterance; the WebView may be recreated.
        TtsController?.Stop();
    }

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        ApplyThemeBackground();
        _ = ApplyThemeStylesAsync();
    }

    private void OnAppCultureChanged(object? sender, EventArgs e)
    {
        _needsRender = true;
        _ = ApplyI18nAsync();
        _ = RunRenderPipelineSafeAsync(StartRenderGeneration());
    }

    public async Task ApplyI18nAsync()
    {
        var generation = Interlocked.Increment(ref _i18nApplyGeneration);
        try
        {
            await EnsureReadyAsync().ConfigureAwait(true);
            if (!await WaitForDocumentReadyAsync().ConfigureAwait(true))
            {
                return;
            }

            if (generation != _i18nApplyGeneration)
            {
                return;
            }

            var script =
                "(function(){ if (typeof applyChatI18n !== 'function') return 'missing'; " +
                "try { " + _htmlBuilder.BuildI18nUpdateScript() + " return 'ok'; } catch (e) { return 'error'; } })();";
            await ChatWebView.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"WebChatView ApplyI18n failed: {ex.Message}");
        }
    }

    public async Task ApplyThemeStylesAsync()
    {
        var generation = Interlocked.Increment(ref _themeApplyGeneration);
        try
        {
            await EnsureReadyAsync().ConfigureAwait(true);
            if (!await WaitForDocumentReadyAsync().ConfigureAwait(true))
            {
                return;
            }

            if (generation != _themeApplyGeneration)
            {
                return;
            }

            var updateScript =
                "(function(){ if (typeof applyThemeUpdate !== 'function') return 'missing'; " +
                "if (!document.getElementById('chat-theme-tokens')) return 'legacy'; " +
                "try { " + _htmlBuilder.BuildThemeUpdateScript() + " return 'ok'; } catch (e) { return 'error'; } })();";
            var result = await ChatWebView.CoreWebView2.ExecuteScriptAsync(updateScript).ConfigureAwait(true);
            if (generation != _themeApplyGeneration)
            {
                return;
            }

            if (result is "\"missing\"" or "\"legacy\"" or "\"error\"")
            {
                _needsRender = true;
                await RunRenderPipelineSafeAsync(StartRenderGeneration()).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            App.StartupTrace($"WebChatView ApplyThemeStyles failed: {ex.Message}");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        AppThemeManager.ThemeChanged += OnAppThemeChanged;
        AppCultureManager.CultureChanged -= OnAppCultureChanged;
        AppCultureManager.CultureChanged += OnAppCultureChanged;
        ApplyThemeBackground();
        _ = ApplyThemeStylesAsync();
        _ = RunRenderPipelineSafeAsync(_renderGeneration);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            // Reported with needsRender because this is one of the few events that can rescue an
            // orphaned render; seeing it fire with needsRender=true is how we learn the rescue worked.
            ChatRenderTrace.Record("visibleChanged", $"now=True needsRender={_needsRender}");
            _ = RunRenderPipelineSafeAsync(_renderGeneration);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_needsRender && CanRender())
        {
            ChatRenderTrace.Record(
                "sizeChangedRescue",
                $"w={ActualWidth:0.##} h={ActualHeight:0.##} gen={_renderGeneration}");
            _ = RunRenderPipelineSafeAsync(_renderGeneration);
        }
    }

    private bool CanRender() =>
        IsVisible && ActualWidth >= 1 && ActualHeight >= 1;

    private int StartRenderGeneration()
    {
        var generation = Interlocked.Increment(ref _renderGeneration);
        lock (_renderBarrierGate)
        {
            // Release waiters from the superseded generation; they will observe
            // the generation mismatch and discard their stale operation.
            _renderBarrier.TrySetResult(false);
            _renderBarrierGeneration = generation;
            _renderBarrier = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return generation;
    }

    private async Task<bool> WaitForRenderGenerationAsync(int generation)
    {
        Task<bool> barrier;
        lock (_renderBarrierGate)
        {
            if (_renderBarrierGeneration != generation)
            {
                // The generation was superseded before we even started waiting. The discarded side
                // used to vanish without a trace, which made "never rendered" and "rendered for a
                // stale generation" look identical.
                ChatRenderTrace.Record(
                    "barrierSuperseded",
                    $"want={generation} current={_renderBarrierGeneration}");
                return false;
            }

            barrier = _renderBarrier.Task;
        }

        var completed = await Task.WhenAny(barrier, Task.Delay(TimeSpan.FromSeconds(5)))
            .ConfigureAwait(true);
        if (!ReferenceEquals(completed, barrier))
        {
            // Was previously silent. A timeout here means the page never reported replayComplete,
            // so distinguishing it from "no render was ever attempted" is essential.
            ChatRenderTrace.Record(
                "barrierTimeout",
                $"gen={generation} doc={_documentReady} needsRender={_needsRender} inProgress={_renderInProgress}");
            return false;
        }

        var rendered = await barrier.ConfigureAwait(true);
        if (!rendered)
        {
            // Released as false: either an explicit supersede or a render that ran without painting.
            ChatRenderTrace.Record("barrierUnrendered", $"gen={generation}");
        }

        return rendered;
    }

    private void CompleteRenderGeneration(int generation, bool rendered)
    {
        lock (_renderBarrierGate)
        {
            if (_renderBarrierGeneration == generation)
            {
                _renderBarrier.TrySetResult(rendered);
            }
        }
    }

    private void ResetRenderBarrierForRetry(int generation)
    {
        lock (_renderBarrierGate)
        {
            if (_renderBarrierGeneration == generation && _renderBarrier.Task.IsCompleted)
            {
                _renderBarrier = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private static TaskCompletionSource<bool> CreateCompletedRenderBarrier()
    {
        var barrier = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        barrier.SetResult(true);
        return barrier;
    }

    private static TaskCompletionSource<bool> CreateCompletedDocumentReadyTcs()
    {
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult(true);
        return tcs;
    }

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
        int? responseDurationMs = null)
    {
        PostTimelineEvent(ChatEventSerializer.SerializeStaticAssistantHtml(message, streaming, responseDurationMs));
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

    /// <summary>
    /// Posts one AG-UI event into the timeline over the WebView message channel.
    ///
    /// This is the single real-time transport. It used to be an <c>ExecuteScriptAsync</c> call
    /// with the event JSON pasted into a <c>handleEvent(...)</c> literal, which meant a live event
    /// and the same replayed event travelled two different contracts. Posting JSON over the same
    /// channel replay uses keeps one contract, and preserves ordering because the WebView message
    /// queue is FIFO.
    /// </summary>
    private void PostTimelineEvent(string eventJson)
    {
        if (string.IsNullOrEmpty(eventJson))
        {
            return;
        }

        _ = PostTimelineEventAsync(eventJson);
    }

    private async Task PostTimelineEventAsync(string eventJson)
    {
        var expectedGeneration = Volatile.Read(ref _renderGeneration);
        try
        {
            await EnsureReadyAsync().ConfigureAwait(true);
            if (!await WaitForDocumentReadyAsync().ConfigureAwait(true)
                || expectedGeneration != Volatile.Read(ref _renderGeneration)
                || !await WaitForRenderGenerationAsync(expectedGeneration).ConfigureAwait(true))
            {
                return;
            }

            var json = ChatEventSerializer.SerializeEventsCommand("event", [eventJson]);
            await _renderOperationGate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (expectedGeneration != Volatile.Read(ref _renderGeneration))
                {
                    return;
                }

                ChatWebView.CoreWebView2.PostWebMessageAsJson(json);
            }
            finally
            {
                _renderOperationGate.Release();
            }
        }
        catch (Exception ex)
        {
            var message = $"WebChatView post timeline event failed: {ex.Message}";
            ScriptExecutionFailed?.Invoke(this, message);
            App.StartupTrace(message);
        }
    }

    private void ScheduleRenderRetry()
    {
        if (_renderRetryScheduled)
        {
            return;
        }

        _renderRetryScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _renderRetryScheduled = false;
            if (_needsRender)
            {
                _ = RunRenderPipelineSafeAsync(_renderGeneration);
            }
        });
    }

    private async Task RunRenderPipelineSafeAsync(int expectedGeneration)
    {
        try
        {
            await EnsureInitializedAndRenderAsync(expectedGeneration).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            CompleteRenderGeneration(expectedGeneration, rendered: false);
            App.StartupTrace($"WebChatView render pipeline failed: {ex}");
            ReportInitializationFailure(Strings.Format("Chat_RenderFailed", ex.Message));
        }
    }

    private async Task EnsureInitializedAndRenderAsync(int expectedGeneration)
    {
        await EnsureReadyAsync().ConfigureAwait(true);
        if (!_needsRender || expectedGeneration != _renderGeneration)
        {
            return;
        }

        ResetRenderBarrierForRetry(expectedGeneration);
        if (!CanRender())
        {
            // Counted on every miss rather than once per transition: the bare chat page holds the
            // WebView at 2x2 while HasChatMessages is false, and a switch clears the collection
            // before refilling it, so this gate is reached on ordinary switches. A single trace
            // could not show that it fired repeatedly.
            ChatRenderTrace.Record(
                "canRenderFalse",
                $"visible={IsVisible} w={ActualWidth:0.##} h={ActualHeight:0.##} gen={expectedGeneration}");

            // The drop is recorded above; this additionally reports it if it never recovers, which is
            // the state the user describes as needing a session switch to clear.
            CompleteRenderGeneration(expectedGeneration, rendered: false);
            WatchForStalledRender(expectedGeneration);
            return;
        }

        if (_renderInProgress)
        {
            _renderQueuedWhileInProgress = true;
            return;
        }

        _renderInProgress = true;
        try
        {
            if (!await WaitForDocumentReadyAsync().ConfigureAwait(true)
                || expectedGeneration != _renderGeneration)
            {
                if (expectedGeneration == _renderGeneration)
                {
                    CompleteRenderGeneration(expectedGeneration, rendered: false);
                }

                return;
            }

            await _renderOperationGate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (expectedGeneration != _renderGeneration)
                {
                    return;
                }

                var messages = _pendingMessages.ToArray();
                var showToolCalls = _pendingShowToolCalls;
                var activitySource = _pendingActivitySourceMessages;
                var planRun = _pendingPlanRun;
                var sessionId = _pendingSessionId;
                var replayCache = _pendingReplayCache;
                await PostReplayInBatchesAsync(messages, showToolCalls, activitySource, planRun, expectedGeneration, sessionId, replayCache)
                    .ConfigureAwait(true);
                if (expectedGeneration != _renderGeneration)
                {
                    return;
                }

                _needsRender = false;
                App.StartupTrace($"WebChatView replayed {_pendingMessages.Count} messages");
                // #endregion
            }
            finally
            {
                _renderOperationGate.Release();
            }
        }
        finally
        {
            _renderInProgress = false;
            if (_needsRender && _renderQueuedWhileInProgress)
            {
                _renderQueuedWhileInProgress = false;
                ScheduleRenderRetry();
            }
            else
            {
                _renderQueuedWhileInProgress = false;
                // Not detectable from inside the pipeline: LoadMessagesAsync only schedules its retry
                // after this call returns, so at this instant a pending retry is normal. Watch
                // _needsRender asynchronously instead and report only if it is *still* pending after
                // a grace period, which is the actual definition of a dropped render.
                WatchForStalledRender(expectedGeneration);
            }
        }
    }

    /// <summary>
    /// Reports <c>orphan</c> when a render is still wanted some time after the pipeline finished.
    ///
    /// <para>This deliberately measures state over time rather than snapshotting it at one instant.
    /// A synchronous check inside the pipeline cannot tell a dropped render from one whose retry is
    /// scheduled a few statements later, and would fire on every ordinary load. The property that
    /// actually matters is "no retry arrived and nothing is running", and only elapsed time shows
    /// it.</para>
    ///
    /// <para>The delay doubles as the measurement: the reported <c>stuck=</c> value is how long the
    /// content stayed unrendered, which is the number needed to tell a flicker from the reported
    /// "switch session to make it appear".</para>
    ///
    /// <para>Reported rather than repaired on purpose. The fix belongs behind evidence of which gate
    /// is actually being hit; acting on the theory would risk papering over a second cause.</para>
    /// </summary>
    private void WatchForStalledRender(int generation)
    {
        if (!_needsRender)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(OrphanWatchDelay);
            if (!_needsRender
                || generation != _renderGeneration
                || _renderInProgress
                || _renderRetryScheduled
                || _renderQueuedWhileInProgress)
            {
                // Resolved (rendered or superseded) or a retry is in flight: nothing was dropped.
                return;
            }

            ChatRenderTrace.Record(
                "orphan",
                $"gen={generation} stuck>{OrphanWatchDelay.TotalMilliseconds:0}ms "
                + $"visible={IsVisible} w={ActualWidth:0.##} h={ActualHeight:0.##} "
                + $"canRender={CanRender()} doc={_documentReady} init={_initialized} "
                + $"msgs={_pendingMessages.Count}");
        });
    }

    private async Task PostReplayInBatchesAsync(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls,
        IReadOnlyList<ChatMessage>? activitySource,
        Athlon.Agent.Core.Plan.PlanRun? planRun,
        int expectedGeneration,
        string? sessionId,
        ChatReplaySnapshotCache? replayCache)
    {
        const int batchSize = ConversationDisplayLimits.WebViewReplayBatchSize;
        // Build the full timeline once so turn folding (final assistant vs activity) stays correct,
        // then post event batches to keep the UI responsive.
        var revision = replayCache is { Enabled: true } && !string.IsNullOrEmpty(sessionId)
            ? ChatReplayRevision.Compute(messages, showToolCalls, activitySource, planRun)
            : null;
        if (revision is not null
            && await TrySwitchSessionSnapshotAsync(sessionId!, revision, expectedGeneration).ConfigureAwait(true))
        {
            // The timeline swapped this session's rendered DOM back in, so there is no replay to run.
            return;
        }

        if (revision is not null && replayCache!.TryGet(sessionId!, revision, out var cachedBatches))
        {
            // Reuse the serialized shards: skip the markdown->HTML replay build entirely.
            await PostCachedBatchesAsync(cachedBatches, expectedGeneration, sessionId, revision).ConfigureAwait(true);
            return;
        }

        IReadOnlyList<string> allEvents;
        using (SessionSwitchProfiler.Measure(SessionSwitchPhases.ReplayBuild))
        {
            allEvents = await Task.Run(
                    () => ChatEventSerializer.BuildReplayEvents(
                        messages,
                        showToolCalls,
                        includeReset: true,
                        activitySourceMessages: activitySource,
                        planRun: planRun))
                .ConfigureAwait(true);
        }

        if (revision is not null && replayCache is { Enabled: true })
        {
            replayCache.Set(sessionId!, revision, SliceBatches(allEvents, batchSize));
        }

        if (expectedGeneration != _renderGeneration)
        {
            return;
        }

        if (allEvents.Count == 0)
        {
            // Zero events means the page receives replayComplete without a single row. That is a
            // legitimate outcome for an empty session, but it is also what a "collection was empty
            // when snapshotted" bug looks like, so the count is always reported.
            ChatRenderTrace.Record(
                "replayEmpty",
                $"gen={expectedGeneration} msgs={messages.Count} session={sessionId ?? "-"}");
            ChatWebView.CoreWebView2.PostWebMessageAsJson(
                ChatEventSerializer.SerializeEventsCommand(
                    "replay",
                    Array.Empty<string>(),
                    expectedGeneration,
                    replayComplete: true,
                    revision: revision));
            return;
        }

        var postedLast = false;
        for (var offset = 0; offset < allEvents.Count; offset += batchSize)
        {
            if (expectedGeneration != _renderGeneration)
            {
                // Bailing out mid-stream leaves replayComplete unsent for this generation, so any
                // waiter on its barrier can only time out. Reported because the abandoned render and
                // a genuinely slow one look the same from the outside otherwise.
                ChatRenderTrace.Record(
                    "batchesAbandoned",
                    $"gen={expectedGeneration} now={_renderGeneration} sent={offset}/{allEvents.Count} lastSent={postedLast}");
                return;
            }

            var take = Math.Min(batchSize, allEvents.Count - offset);
            var slice = allEvents.Skip(offset).Take(take).ToArray();
            var isFirst = offset == 0;
            var isLast = offset + take >= allEvents.Count;
            var json = await Task.Run(() => ChatEventSerializer.SerializeEventsCommand(
                    isFirst ? "replay" : "append",
                    slice,
                    expectedGeneration,
                    replayComplete: isLast,
                    sessionId: isFirst ? sessionId : null,
                    revision: isLast ? revision : null))
                .ConfigureAwait(true);
            if (expectedGeneration != _renderGeneration)
            {
                ChatRenderTrace.Record(
                    "batchesAbandoned",
                    $"gen={expectedGeneration} now={_renderGeneration} sent={offset}/{allEvents.Count} lastSent={postedLast}");
                return;
            }

            using (SessionSwitchProfiler.Measure(SessionSwitchPhases.PostBatches))
            {
                ChatWebView.CoreWebView2.PostWebMessageAsJson(json);
            }

            postedLast = isLast;

            if (offset + take < allEvents.Count)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        ChatRenderTrace.Record(
            "batchesPosted",
            $"gen={expectedGeneration} events={allEvents.Count} batches={(allEvents.Count + batchSize - 1) / batchSize} complete={postedLast}");
    }

    /// <summary>
    /// Slices a full replay event stream into the fixed-size shards the timeline is fed in. Shards
    /// (not the final command JSON) are cached because the render generation changes on every render
    /// and would otherwise poison the cache.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> SliceBatches(
        IReadOnlyList<string> allEvents,
        int batchSize)
    {
        if (allEvents.Count == 0)
        {
            return Array.Empty<IReadOnlyList<string>>();
        }

        var batches = new List<IReadOnlyList<string>>((allEvents.Count + batchSize - 1) / batchSize);
        for (var offset = 0; offset < allEvents.Count; offset += batchSize)
        {
            var take = Math.Min(batchSize, allEvents.Count - offset);
            var slice = new string[take];
            for (var i = 0; i < take; i++)
            {
                slice[i] = allEvents[offset + i];
            }

            batches.Add(slice);
        }

        return batches;
    }

    /// <summary>
    /// Tells the page to drop a session's saved DOM snapshot (its controller was released or
    /// evicted). Fire-and-forget: a page that is gone or shutting down is not an error.
    /// </summary>
    public void InvalidateSessionSnapshotInPage(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || ChatWebView?.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            ChatWebView.CoreWebView2.PostWebMessageAsJson(
                ChatEventSerializer.SerializeInvalidateSessionCommand(sessionId));
        }
        catch (Exception ex)
        {
            App.StartupTrace($"invalidateSession post failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks the timeline to swap a previously rendered session back in (the Phase-4 fast path, which
    /// is reached only when the replay cache switch is on, i.e. <c>Ui.FastSessionSwitch</c> &amp;&amp;
    /// <c>Ui.CacheReplayEvents</c>). Returns true only when the page answered
    /// <c>snapshotRestored</c> for the current generation; every other outcome (miss, timeout,
    /// superseded render) returns false so the caller falls back to the authoritative replay.
    /// </summary>
    private async Task<bool> TrySwitchSessionSnapshotAsync(string sessionId, string revision, int expectedGeneration)
    {
        if (expectedGeneration != _renderGeneration || ChatWebView?.CoreWebView2 is null)
        {
            return false;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSnapshotSwitch = completion;
        try
        {
            ChatWebView.CoreWebView2.PostWebMessageAsJson(
                ChatEventSerializer.SerializeSwitchSessionCommand(sessionId, revision, expectedGeneration));

            var finished = await Task.WhenAny(completion.Task, Task.Delay(SnapshotSwitchTimeout)).ConfigureAwait(true);
            if (finished != completion.Task || expectedGeneration != _renderGeneration)
            {
                return false;
            }

            var restored = await completion.Task.ConfigureAwait(true);
            if (restored)
            {
                SessionSwitchProfiler.SetHitKind(SessionSwitchHitKind.Snapshot);
                // This path sends no replayComplete (nothing was replayed), so the render barrier is
                // completed here to release any waiter on this generation.
                CompleteRenderGeneration(expectedGeneration, rendered: true);
            }

            return restored;
        }
        finally
        {
            _pendingSnapshotSwitch = null;
        }
    }

    /// <summary>
    /// Posts a cached shard set. Serialization (and the render generation stamped into each command)
    /// still runs per render; only the expensive markdown-to-HTML replay build is skipped.
    /// </summary>
    private async Task PostCachedBatchesAsync(
        IReadOnlyList<IReadOnlyList<string>> batches,
        int expectedGeneration,
        string? sessionId,
        string? revision)
    {
        if (expectedGeneration != _renderGeneration)
        {
            return;
        }

        if (batches.Count == 0)
        {
            ChatWebView.CoreWebView2.PostWebMessageAsJson(
                ChatEventSerializer.SerializeEventsCommand(
                    "replay",
                    Array.Empty<string>(),
                    expectedGeneration,
                    replayComplete: true,
                    revision: revision));
            return;
        }

        for (var index = 0; index < batches.Count; index++)
        {
            if (expectedGeneration != _renderGeneration)
            {
                return;
            }

            var slice = batches[index];
            var isFirst = index == 0;
            var isLast = index == batches.Count - 1;
            var json = await Task.Run(() => ChatEventSerializer.SerializeEventsCommand(
                    isFirst ? "replay" : "append",
                    slice,
                    expectedGeneration,
                    replayComplete: isLast,
                    sessionId: isFirst ? sessionId : null,
                    revision: isLast ? revision : null))
                .ConfigureAwait(true);
            if (expectedGeneration != _renderGeneration)
            {
                return;
            }

            using (SessionSwitchProfiler.Measure(SessionSwitchPhases.PostBatches))
            {
                ChatWebView.CoreWebView2.PostWebMessageAsJson(json);
            }

            if (!isLast)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
    }

    private async Task EnsureReadyAsync()
    {
        if (_initialized)
        {
            return;
        }

        if (_initTask is { IsFaulted: true } or { IsCanceled: true })
        {
            _initTask = null;
        }

        _initTask ??= InitializeWebViewAsync();
        await _initTask.ConfigureAwait(true);
    }

    private async Task InitializeWebViewAsync()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            await WebView2Initializer.EnsureCoreWebView2Async(ChatWebView).ConfigureAwait(true);
            ChatWebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            ChatWebView.CoreWebView2.Settings.IsScriptEnabled = true;
            ChatWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            ChatWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            ChatWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            ChatWebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            ChatWebView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            var assetsDir = ChatMarkdownAssets.AssetsDirectory;
            if (Directory.Exists(assetsDir))
            {
                ChatWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    ChatMarkdownAssets.VirtualHost,
                    assetsDir,
                    CoreWebView2HostResourceAccessKind.Allow);
            }

            // The bundled Mermaid runtime lives in its own folder (shared with the preview
            // window) and gets its own host so the timeline can lazy-load it on demand.
            var mermaidDir = ChatMarkdownAssets.MermaidAssetsDirectory;
            if (Directory.Exists(mermaidDir))
            {
                ChatWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    ChatMarkdownAssets.MermaidVirtualHost,
                    mermaidDir,
                    CoreWebView2HostResourceAccessKind.Allow);
            }

            ApplyThemeBackground();
            await NavigateShellAsync().ConfigureAwait(true);
            _initialized = true;
            App.StartupTrace("WebChatView initialization completed");
        }
        catch (Exception ex)
        {
            _initTask = null;
            App.StartupTrace($"WebChatView initialization failed: {ex}");
            ReportInitializationFailure(Strings.Format("Chat_RenderInitFailed", ex.Message));
            throw;
        }
    }

    private void ReportInitializationFailure(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ReportInitializationFailure(message));
            return;
        }

        InitializationFailed?.Invoke(this, message);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(e.WebMessageAsJson);
            }
            catch (JsonException)
            {
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type))
                {
                    return;
                }

                switch (type.GetString())
                {
                    case "replayComplete":
                        var renderMs = 0d;
                        if (root.TryGetProperty("renderMs", out var renderMsElement)
                            && renderMsElement.TryGetDouble(out var parsedRenderMs))
                        {
                            renderMs = parsedRenderMs;
                            SessionSwitchProfiler.Record(SessionSwitchPhases.JsRender, renderMs);
                        }

                        if (root.TryGetProperty("renderGeneration", out var generationElement)
                            && generationElement.TryGetInt32(out var completedGeneration))
                        {
                            ChatRenderTrace.Record(
                                "replayComplete",
                                $"gen={completedGeneration} current={_renderGeneration} renderMs={renderMs:0.#}");
                            CompleteRenderGeneration(completedGeneration, rendered: true);
                        }
                        else
                        {
                            // A completion without a generation can never release a barrier, so it is
                            // reported rather than silently ignored.
                            ChatRenderTrace.Record("replayCompleteNoGen", $"current={_renderGeneration}");
                        }

                        break;
                    case "renderIssue":
                        // Page-side failure or anomaly. Logged verbatim so the JS report and the C#
                        // gate reports read as one timeline in the same file.
                        var issueKind = root.TryGetProperty("kind", out var kindElement)
                            ? kindElement.GetString()
                            : "unknown";
                        var issueDetail = root.TryGetProperty("detail", out var detailElement)
                            ? detailElement.GetString()
                            : "";
                        ChatRenderTrace.Record("page", $"kind={issueKind} detail={issueDetail}");
                        break;
                    case "snapshotRestored":
                        _pendingSnapshotSwitch?.TrySetResult(true);
                        break;
                    case "snapshotMiss":
                        _pendingSnapshotSwitch?.TrySetResult(false);
                        break;
                    case "copy":
                        var text = root.TryGetProperty("text", out var textElement)
                            ? textElement.GetString()
                            : null;
                        if (!string.IsNullOrEmpty(text))
                        {
                            Clipboard.SetText(text);
                        }

                        break;
                    case "playAudio":
                        var ttsMessageId = root.TryGetProperty("messageId", out var ttsMessageIdElement)
                            ? ttsMessageIdElement.GetString()
                            : null;
                        var ttsText = root.TryGetProperty("text", out var ttsTextElement)
                            ? ttsTextElement.GetString()
                            : null;
                        if (!string.IsNullOrEmpty(ttsMessageId) && !string.IsNullOrEmpty(ttsText))
                        {
                            _ = TtsController?.PlayAsync(ttsMessageId, ttsText);
                        }

                        break;
                    case "stopAudio":
                        TtsController?.Stop();
                        break;
                    case "preview":
                        var html = root.TryGetProperty("html", out var htmlElement)
                            ? htmlElement.GetString()
                            : null;
                        if (!string.IsNullOrEmpty(html))
                        {
                            Dispatcher.BeginInvoke(
                                () => Windows.HtmlPreviewWindow.Show(html, Window.GetWindow(this)),
                                DispatcherPriority.Normal);
                        }

                        break;
                    case "loadOlder":
                        OlderMessagesRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    case "openUrl":
                        var openUrl = root.TryGetProperty("url", out var openUrlElement)
                            ? openUrlElement.GetString()
                            : null;
                        RequestOpenExternalLink(openUrl);
                        break;
                    case "toolApproval":
                        var toolCallId = root.TryGetProperty("toolCallId", out var toolCallIdElement)
                            ? toolCallIdElement.GetString()
                            : null;
                        var approved = root.TryGetProperty("approved", out var approvedElement)
                            && approvedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                            && approvedElement.GetBoolean();
                        if (!string.IsNullOrWhiteSpace(toolCallId))
                        {
                            ToolApprovalDecisionReceived?.Invoke(
                                this,
                                new ToolApprovalDecisionEventArgs(
                                    toolCallId,
                                    approved ? ToolApprovalDecision.Approved : ToolApprovalDecision.Denied));
                        }

                        break;
                    case "planBuild":
                        PlanBuildRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    case "planRevise":
                        PlanReviseRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    case "requestToolDetail":
                    {
                        var detailMessageId = root.TryGetProperty("messageId", out var detailMessageIdElement)
                            ? detailMessageIdElement.GetString()
                            : null;
                        var detailToolCallId = root.TryGetProperty("toolCallId", out var detailToolCallIdElement)
                            ? detailToolCallIdElement.GetString()
                            : null;
                        var requestId = root.TryGetProperty("requestId", out var requestIdElement)
                            ? requestIdElement.GetString()
                            : null;
                        if (!string.IsNullOrWhiteSpace(detailMessageId)
                            || !string.IsNullOrWhiteSpace(detailToolCallId))
                        {
                            ToolDetailRequested?.Invoke(
                                this,
                                new ToolDetailRequestEventArgs(detailMessageId, detailToolCallId, requestId));
                        }

                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.StartupTrace($"WebChatView copy message failed: {ex.Message}");
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsChatShellNavigation(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        RequestOpenExternalLink(e.Uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        RequestOpenExternalLink(e.Uri);
    }

    private void RequestOpenExternalLink(string? uri)
    {
        if (!TryGetHttpUrl(uri, out var httpUrl))
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            ExternalLinkRequested?.Invoke(this, httpUrl);
            return;
        }

        Dispatcher.BeginInvoke(() => ExternalLinkRequested?.Invoke(this, httpUrl));
    }

    private static bool IsChatShellNavigation(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return true;
        }

        if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return uri.StartsWith(ChatMarkdownAssets.VirtualBaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetHttpUrl(string? uri, out string httpUrl)
    {
        httpUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var absolute))
        {
            return false;
        }

        if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        httpUrl = absolute.AbsoluteUri;
        return true;
    }

    private void ApplyThemeBackground()
    {
        var chatBg = AppThemeManager.Current.Chrome.ChatBackgroundTop;
        ChatWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
            chatBg.A,
            chatBg.R,
            chatBg.G,
            chatBg.B);
        Background = new SolidColorBrush(chatBg);
    }

    private async Task ExecuteScriptWhenReadyAsync(string script)
    {
        var expectedGeneration = Volatile.Read(ref _renderGeneration);
        try
        {
            await EnsureReadyAsync().ConfigureAwait(true);
            var documentReady = await WaitForDocumentReadyAsync().ConfigureAwait(true);
            if (!documentReady || expectedGeneration != Volatile.Read(ref _renderGeneration))
            {
                App.StartupTrace(
                    $"WebChatView ExecuteScript skipped: stale generation or document not ready ({script.Length} chars)");
                return;
            }

            if (!await WaitForRenderGenerationAsync(expectedGeneration).ConfigureAwait(true)
                || expectedGeneration != Volatile.Read(ref _renderGeneration))
            {
                return;
            }

            await _renderOperationGate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (expectedGeneration != Volatile.Read(ref _renderGeneration))
                {
                    return;
                }

                await ChatWebView.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
            }
            finally
            {
                _renderOperationGate.Release();
            }
        }
        catch (Exception ex)
        {
            var message = $"WebChatView ExecuteScript failed ({script.Length} chars): {ex.Message}";
            ScriptExecutionFailed?.Invoke(this, message);
            App.StartupTrace(message);
        }
    }

    private async Task<bool> WaitForDocumentReadyAsync()
    {
        if (_documentReady)
        {
            return true;
        }

        var generation = _navigationGeneration;
        var deadline = Task.Delay(TimeSpan.FromSeconds(5));
        var readyTask = _documentReadyTcs.Task;
        var completed = await Task.WhenAny(readyTask, deadline).ConfigureAwait(true);
        if (!ReferenceEquals(completed, readyTask))
        {
            const string timeoutMessage = "WebChatView WaitForDocumentReady timed out after 5s";
            ScriptExecutionFailed?.Invoke(this, timeoutMessage);
            App.StartupTrace(timeoutMessage);
            return false;
        }

        // Only trust the result if the navigation generation hasn't advanced (a newer
        // navigation may have reset the TCS and this completion belongs to a stale one).
        if (generation != _navigationGeneration)
        {
            return false;
        }

        return _documentReady && _documentReadyTcs.Task.IsCompletedSuccessfully;
    }

    private async Task NavigateShellAsync()
    {
        var generation = ++_navigationGeneration;
        _documentReady = false;
        // Reset the completion source so WaitForDocumentReadyAsync awaits this navigation.
        _documentReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            ChatWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            if (generation == _navigationGeneration)
            {
                _documentReady = e.IsSuccess;
                if (!e.IsSuccess)
                {
                    App.StartupTrace($"WebChatView navigation failed: {e.WebErrorStatus}");
                }
            }

            _documentReadyTcs.TrySetResult(e.IsSuccess);
        }

        try
        {
            ChatWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            ChatWebView.NavigateToString(
                _htmlBuilder.BuildShellHtml(ResolveSsoDisplayName(), TtsController?.Enabled ?? false));
            var success = await _documentReadyTcs.Task.ConfigureAwait(true);
            if (!success || generation != _navigationGeneration)
            {
                throw new InvalidOperationException("WebChatView shell navigation failed.");
            }
        }
        catch (Exception ex)
        {
            ChatWebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            App.StartupTrace($"WebChatView shell navigation failed: {ex}");
            throw;
        }

        // Re-attach read-aloud after every navigation: the previous page (and its post target) is gone.
        // The enabled flag was already baked into the shell by the line above, so no script push is
        // needed here.
        //
        // Deliberately NOT awaiting PushTtsConfigAsync(): it goes through ExecuteScriptWhenReadyAsync,
        // which awaits EnsureReadyAsync(), which awaits _initTask -- and _initTask is the very
        // InitializeWebViewAsync call that is executing this method. _initialized only flips true
        // after this method returns, so awaiting it here deadlocks the UI thread and every later
        // render/replay (including session switches) blocks forever behind it.
        AttachTtsTransport();
    }

    /// <summary>
    /// Points the controller's status channel at this WebView instance, so a recreated page does not
    /// keep pushing state into a disposed one.
    /// </summary>
    private void AttachTtsTransport() =>
        TtsController?.AttachPostTarget(json => PostToPageAsync(json));

    /// <summary>
    /// Tells the timeline whether the read-aloud button should exist. Called after settings are
    /// saved, so toggling the feature takes effect without a page reload.
    /// </summary>
    /// <remarks>
    /// Guarded on <c>_initialized</c> rather than calling <c>EnsureReadyAsync</c> unconditionally.
    /// During shell startup this method would await <c>_initTask</c> -- the very navigation that is
    /// still executing -- and deadlock the UI thread, which stalls every later render and session
    /// switch too. Before initialization the flag is already embedded in the shell HTML by
    /// <see cref="ChatHtmlBuilder.BuildShellHtml"/>, so skipping is correct rather than lossy.
    /// </remarks>
    public Task PushTtsConfigAsync()
    {
        if (!_initialized)
        {
            ChatRenderTrace.Record("ttsConfigSkipped", "view not initialized; flag comes from shell HTML");
            return Task.CompletedTask;
        }

        return ExecuteScriptWhenReadyAsync(ChatHtmlBuilder.BuildTtsConfigScript(TtsController?.Enabled ?? false));
    }

    private Task PostToPageAsync(string json)
    {
        var core = ChatWebView?.CoreWebView2;
        if (core is null)
        {
            return Task.CompletedTask;
        }

        core.PostWebMessageAsJson(json);
        return Task.CompletedTask;
    }

    public Task ScrollToBottomAsync() =>
        ExecuteScriptWhenReadyAsync("scrollToBottom();");

    public Task ScrollToBottomImmediateAsync() =>
        ExecuteScriptWhenReadyAsync("scrollToBottom(true);");

    public async Task PostToolDetailAsync(
        string? requestId,
        string? messageId,
        string? toolCallId,
        string? content)
    {
        try
        {
            await EnsureReadyAsync().ConfigureAwait(true);
            if (ChatWebView.CoreWebView2 is null)
            {
                return;
            }

            var payload = JsonSerializer.Serialize(new
            {
                command = "toolDetail",
                requestId,
                messageId,
                toolCallId,
                content = content ?? string.Empty
            });
            ChatWebView.CoreWebView2.PostWebMessageAsJson(payload);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"WebChatView PostToolDetail failed: {ex.Message}");
        }
    }

    public async Task PrependMessagesAsync(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls,
        bool hasOlderMessages)
    {
        var expectedGeneration = Volatile.Read(ref _renderGeneration);
        if (messages.Count == 0)
        {
            await SetOlderMessagesAvailableAsync(hasOlderMessages, expectedGeneration).ConfigureAwait(true);
            return;
        }

        try
        {
            await EnsureReadyAsync().ConfigureAwait(true);
            if (!await WaitForDocumentReadyAsync().ConfigureAwait(true)
                || expectedGeneration != Volatile.Read(ref _renderGeneration))
            {
                return;
            }

            var snapshot = messages.ToArray();
            var json = await Task.Run(
                () => ChatEventSerializer.SerializePrependCommand(snapshot, showToolCalls, hasOlderMessages))
                .ConfigureAwait(true);
            if (expectedGeneration != Volatile.Read(ref _renderGeneration))
            {
                return;
            }

            if (!await WaitForRenderGenerationAsync(expectedGeneration).ConfigureAwait(true)
                || expectedGeneration != Volatile.Read(ref _renderGeneration))
            {
                return;
            }

            await _renderOperationGate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (expectedGeneration != Volatile.Read(ref _renderGeneration))
                {
                    return;
                }

                ChatWebView.CoreWebView2.PostWebMessageAsJson(json);
            }
            finally
            {
                _renderOperationGate.Release();
            }
        }
        catch (Exception ex)
        {
            var message = $"WebChatView prepend history failed: {ex.Message}";
            ScriptExecutionFailed?.Invoke(this, message);
            App.StartupTrace(message);
        }
    }

    public Task SetOlderMessagesAvailableAsync(bool hasOlderMessages) =>
        SetOlderMessagesAvailableAsync(
            hasOlderMessages,
            Volatile.Read(ref _renderGeneration));

    private async Task SetOlderMessagesAvailableAsync(bool hasOlderMessages, int expectedGeneration)
    {
        try
        {
            await EnsureReadyAsync().ConfigureAwait(true);
            if (!await WaitForDocumentReadyAsync().ConfigureAwait(true)
                || expectedGeneration != Volatile.Read(ref _renderGeneration))
            {
                return;
            }

            if (!await WaitForRenderGenerationAsync(expectedGeneration).ConfigureAwait(true)
                || expectedGeneration != Volatile.Read(ref _renderGeneration))
            {
                return;
            }

            await _renderOperationGate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (expectedGeneration != Volatile.Read(ref _renderGeneration))
                {
                    return;
                }

                ChatWebView.CoreWebView2.PostWebMessageAsJson(
                    ChatEventSerializer.SerializeHistoryAvailabilityCommand(hasOlderMessages));
            }
            finally
            {
                _renderOperationGate.Release();
            }
        }
        catch (Exception ex)
        {
            var message = $"WebChatView history availability failed: {ex.Message}";
            ScriptExecutionFailed?.Invoke(this, message);
            App.StartupTrace(message);
        }
    }

    private static string? ResolveSsoDisplayName()
    {
        if (Application.Current is not App { Services: { } services })
        {
            return null;
        }

        return services.GetService<ICurrentSsoUserContext>()?.DisplayName;
    }
}

public sealed record ToolApprovalDecisionEventArgs(
    string ToolCallId,
    ToolApprovalDecision Decision);

public sealed record ToolDetailRequestEventArgs(
    string? MessageId,
    string? ToolCallId,
    string? RequestId);
