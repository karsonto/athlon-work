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
    public event EventHandler<string>? ForkChatRequested;
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


}

public sealed record ToolApprovalDecisionEventArgs(
    string ToolCallId,
    ToolApprovalDecision Decision);

public sealed record ToolDetailRequestEventArgs(
    string? MessageId,
    string? ToolCallId,
    string? RequestId);
