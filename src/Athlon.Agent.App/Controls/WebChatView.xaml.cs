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
    private bool _loggedCanRenderBlock;
    private int _navigationGeneration;
    private int _renderGeneration;
    private readonly SemaphoreSlim _renderOperationGate = new(1, 1);
    private readonly object _renderBarrierGate = new();
    private int _renderBarrierGeneration;
    private TaskCompletionSource<bool> _renderBarrier = CreateCompletedRenderBarrier();
    private IReadOnlyList<ChatMessageViewModel> _pendingMessages = Array.Empty<ChatMessageViewModel>();
    private bool _pendingShowToolCalls;
    private IReadOnlyList<ChatMessage>? _pendingActivitySourceMessages;
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
    public event EventHandler<PlanClarifyAnswerEventArgs>? PlanClarifyAnswerReceived;
    public event EventHandler? PlanBuildRequested;
    public event EventHandler<string>? PlanOpenEditorRequested;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        AppCultureManager.CultureChanged -= OnAppCultureChanged;
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
            _ = RunRenderPipelineSafeAsync(_renderGeneration);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_needsRender && CanRender())
        {
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
                return false;
            }

            barrier = _renderBarrier.Task;
        }

        var completed = await Task.WhenAny(barrier, Task.Delay(TimeSpan.FromSeconds(5)))
            .ConfigureAwait(true);
        return ReferenceEquals(completed, barrier) && await barrier.ConfigureAwait(true);
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

    public async Task LoadMessagesAsync(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls = false,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null)
    {
        // Keep the live per-session collection so a deferred replay (for example after
        // the control becomes visible) snapshots the newest bubbles, not the state from
        // when rendering was first requested.
        _pendingMessages = messages;
        _pendingShowToolCalls = showToolCalls;
        _pendingActivitySourceMessages = activitySourceMessages;
        _needsRender = true;
        var generation = StartRenderGeneration();
        await RunRenderPipelineSafeAsync(generation).ConfigureAwait(true);

        if (_needsRender && generation == _renderGeneration)
        {
            ScheduleRenderRetry();
        }
    }

    public Task ApplyAssistantMarkdownAsync(
        ChatMessageViewModel message,
        bool streaming = false,
        int? responseDurationMs = null) =>
        ExecuteScriptWhenReadyAsync(
            $"handleEvent({ChatEventSerializer.SerializeStaticAssistantHtml(message, streaming, responseDurationMs)});");

    public Task ApplyToolResultMarkdownAsync(ChatMessageViewModel message) =>
        ExecuteScriptWhenReadyAsync($"handleEvent({ChatEventSerializer.SerializeToolResultMarkdown(message)});");

    public Task DispatchUserMessageAsync(ChatMessageViewModel message) =>
        ExecuteScriptWhenReadyAsync($"handleEvent({ChatEventSerializer.SerializeUserMessage(message)});");

    public Task DispatchFilesChangedAsync(IReadOnlyList<ModifiedFileViewModel> files, bool upsert = true)
    {
        // Empty upsert: nothing to show. Empty seal must still reach JS so a live card
        // is finalized and cannot be stolen by the next turn.
        if (files.Count == 0 && upsert)
        {
            return Task.CompletedTask;
        }

        return ExecuteScriptWhenReadyAsync(
            $"handleEvent({ChatEventSerializer.SerializeFilesChanged(files, upsert)});");
    }

    public Task DispatchTurnActivityAsync(TurnActivitySummary summary, bool upsert = true)
    {
        if (!summary.HasContent)
        {
            return Task.CompletedTask;
        }

        return ExecuteScriptWhenReadyAsync(
            $"handleEvent({ChatEventSerializer.SerializeTurnActivity(summary, upsert)});");
    }

    public Task RemoveAssistantBubblesAsync(IReadOnlyList<string> messageIds)
    {
        if (messageIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        return ExecuteScriptWhenReadyAsync(
            $"handleEvent({ChatEventSerializer.SerializeRemoveAssistantBubbles(messageIds)});");
    }

    public Task DispatchEventAsync(AgentStreamEvent streamEvent) =>
        ExecuteScriptWhenReadyAsync(_htmlBuilder.BuildDispatchScript(streamEvent));

    public Task ShowToolApprovalAsync(PendingToolApproval approval, string arguments) =>
        ExecuteScriptWhenReadyAsync(
            $"handleEvent({ChatEventSerializer.SerializeToolApprovalRequest(approval, arguments)});");

    public Task ResolveToolApprovalAsync(string toolCallId, ToolApprovalDecision decision) =>
        ExecuteScriptWhenReadyAsync(
            $"handleEvent({ChatEventSerializer.SerializeToolApprovalResolved(toolCallId, decision)});");

    public Task ShowPlanClarifyAsync(Athlon.Agent.Core.Plan.PlanClarification clarification, bool resolved = false) =>
        ExecuteScriptWhenReadyAsync(
            $"handleEvent({ChatEventSerializer.SerializePlanClarifyRequest(clarification, resolved)});");

    public Task ResolvePlanClarifyAsync(string requestId, string? summary = null) =>
        ExecuteScriptWhenReadyAsync(
            $"handleEvent({ChatEventSerializer.SerializePlanClarifyResolved(requestId, summary)});");

    public Task ShowPlanReadyAsync(Athlon.Agent.Core.Plan.PlanRun run) =>
        ExecuteScriptWhenReadyAsync(
            $"handleEvent({ChatEventSerializer.SerializePlanReady(run)});");

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
            if (!_loggedCanRenderBlock)
            {
                _loggedCanRenderBlock = true;
                App.StartupTrace(
                    $"WebChatView CanRender=false (visible={IsVisible}, width={ActualWidth:0.##}, height={ActualHeight:0.##})");
            }

            CompleteRenderGeneration(expectedGeneration, rendered: false);
            return;
        }

        _loggedCanRenderBlock = false;

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
                await PostReplayInBatchesAsync(messages, showToolCalls, activitySource, expectedGeneration)
                    .ConfigureAwait(true);
                if (expectedGeneration != _renderGeneration)
                {
                    return;
                }

                _needsRender = false;
                App.StartupTrace($"WebChatView replayed {_pendingMessages.Count} messages");
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
            }
        }
    }

    private async Task PostReplayInBatchesAsync(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls,
        IReadOnlyList<ChatMessage>? activitySource,
        int expectedGeneration)
    {
        const int batchSize = ConversationDisplayLimits.WebViewReplayBatchSize;
        // Build the full timeline once so turn folding (final assistant vs activity) stays correct,
        // then post event batches to keep the UI responsive.
        var allEvents = await Task.Run(
                () => ChatEventSerializer.BuildReplayEvents(
                    messages,
                    showToolCalls,
                    includeReset: true,
                    activitySourceMessages: activitySource,
                    mode: TimelineProjectionMode.HighFidelity))
            .ConfigureAwait(true);
        if (expectedGeneration != _renderGeneration)
        {
            return;
        }

        if (allEvents.Count == 0)
        {
            ChatWebView.CoreWebView2.PostWebMessageAsJson(
                ChatEventSerializer.SerializeEventsCommand(
                    "replay",
                    Array.Empty<string>(),
                    expectedGeneration,
                    replayComplete: true));
            return;
        }

        for (var offset = 0; offset < allEvents.Count; offset += batchSize)
        {
            if (expectedGeneration != _renderGeneration)
            {
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
                    replayComplete: isLast))
                .ConfigureAwait(true);
            if (expectedGeneration != _renderGeneration)
            {
                return;
            }

            ChatWebView.CoreWebView2.PostWebMessageAsJson(json);
            if (offset + take < allEvents.Count)
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
                        if (root.TryGetProperty("renderGeneration", out var generationElement)
                            && generationElement.TryGetInt32(out var completedGeneration))
                        {
                            CompleteRenderGeneration(completedGeneration, rendered: true);
                        }

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
                    case "planClarifyAnswer":
                    {
                        var requestId = root.TryGetProperty("requestId", out var requestIdEl)
                            ? requestIdEl.GetString()
                            : null;
                        var freeText = root.TryGetProperty("freeText", out var freeTextEl)
                            ? freeTextEl.GetString()
                            : null;
                        var selections = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                        if (root.TryGetProperty("selections", out var selectionsEl)
                            && selectionsEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in selectionsEl.EnumerateObject())
                            {
                                if (prop.Value.ValueKind != JsonValueKind.Array)
                                {
                                    continue;
                                }

                                var ids = prop.Value.EnumerateArray()
                                    .Select(item => item.GetString())
                                    .Where(id => !string.IsNullOrWhiteSpace(id))
                                    .Select(id => id!)
                                    .ToList();
                                if (ids.Count > 0)
                                {
                                    selections[prop.Name] = ids;
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(requestId))
                        {
                            PlanClarifyAnswerReceived?.Invoke(
                                this,
                                new PlanClarifyAnswerEventArgs(requestId, selections, freeText));
                        }

                        break;
                    }
                    case "planBuild":
                        PlanBuildRequested?.Invoke(this, EventArgs.Empty);
                        break;
                    case "planOpenEditor":
                    {
                        var path = root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            PlanOpenEditorRequested?.Invoke(this, path);
                        }

                        break;
                    }
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
        var deadline = Environment.TickCount64 + 5000;
        while (!_documentReady && generation == _navigationGeneration && Environment.TickCount64 < deadline)
        {
            await Task.Delay(16).ConfigureAwait(true);
        }

        if (!_documentReady && generation == _navigationGeneration)
        {
            const string timeoutMessage = "WebChatView WaitForDocumentReady timed out after 5s";
            ScriptExecutionFailed?.Invoke(this, timeoutMessage);
            App.StartupTrace(timeoutMessage);
            return false;
        }

        return _documentReady;
    }

    private async Task NavigateShellAsync()
    {
        var generation = ++_navigationGeneration;
        _documentReady = false;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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

            tcs.TrySetResult(e.IsSuccess);
        }

        try
        {
            ChatWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            ChatWebView.NavigateToString(_htmlBuilder.BuildShellHtml(ResolveSsoDisplayName()));
            var success = await tcs.Task.ConfigureAwait(true);
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

public sealed record PlanClarifyAnswerEventArgs(
    string RequestId,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Selections,
    string? FreeText);
