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

/// <summary>Render pipeline, replay batches and session snapshot switching.</summary>
public partial class WebChatView
{
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
}
