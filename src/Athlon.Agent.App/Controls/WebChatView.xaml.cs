using System.IO;
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
    public event EventHandler? OlderMessagesRequested;
    public event EventHandler<ToolApprovalDecisionEventArgs>? ToolApprovalDecisionReceived;

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
        _ = RunRenderPipelineSafeAsync(Interlocked.Increment(ref _renderGeneration));
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
                await RunRenderPipelineSafeAsync(Interlocked.Increment(ref _renderGeneration)).ConfigureAwait(true);
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

    public async Task LoadMessagesAsync(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls = false,
        IReadOnlyList<ChatMessage>? activitySourceMessages = null)
    {
        _pendingMessages = messages.ToArray();
        _pendingShowToolCalls = showToolCalls;
        _pendingActivitySourceMessages = activitySourceMessages;
        _needsRender = true;
        var generation = Interlocked.Increment(ref _renderGeneration);
        await RunRenderPipelineSafeAsync(generation).ConfigureAwait(true);

        if (_needsRender && generation == _renderGeneration)
        {
            ScheduleRenderRetry();
        }
    }

    public Task ApplyAssistantMarkdownAsync(ChatMessageViewModel message, bool streaming = false) =>
        ExecuteScriptWhenReadyAsync(
            $"handleEvent({ChatEventSerializer.SerializeStaticAssistantHtml(message, streaming)});");

    public Task ApplyToolResultMarkdownAsync(ChatMessageViewModel message) =>
        ExecuteScriptWhenReadyAsync($"handleEvent({ChatEventSerializer.SerializeToolResultMarkdown(message)});");

    public Task DispatchUserMessageAsync(ChatMessageViewModel message) =>
        ExecuteScriptWhenReadyAsync($"handleEvent({ChatEventSerializer.SerializeUserMessage(message)});");

    public Task DispatchFilesChangedAsync(IReadOnlyList<ModifiedFileViewModel> files, bool upsert = true)
    {
        if (files.Count == 0)
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

    public Task DispatchEventAsync(AgentStreamEvent streamEvent) =>
        ExecuteScriptWhenReadyAsync(_htmlBuilder.BuildDispatchScript(streamEvent));

    public Task ShowToolApprovalAsync(PendingToolApproval approval, string arguments) =>
        ExecuteScriptWhenReadyAsync(
            $"handleEvent({ChatEventSerializer.SerializeToolApprovalRequest(approval, arguments)});");

    public Task ResolveToolApprovalAsync(string toolCallId, ToolApprovalDecision decision) =>
        ExecuteScriptWhenReadyAsync(
            $"handleEvent({ChatEventSerializer.SerializeToolApprovalResolved(toolCallId, decision)});");

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

        if (!CanRender())
        {
            if (!_loggedCanRenderBlock)
            {
                _loggedCanRenderBlock = true;
                App.StartupTrace(
                    $"WebChatView CanRender=false (visible={IsVisible}, width={ActualWidth:0.##}, height={ActualHeight:0.##})");
            }

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
                return;
            }

            var messages = _pendingMessages;
            var showToolCalls = _pendingShowToolCalls;
            var activitySource = _pendingActivitySourceMessages;
            var replayJson = await Task.Run(
                () => ChatEventSerializer.SerializeReplayCommand(messages, showToolCalls, activitySource))
                .ConfigureAwait(true);
            if (expectedGeneration != _renderGeneration)
            {
                return;
            }

            ChatWebView.CoreWebView2.PostWebMessageAsJson(replayJson);
            _needsRender = false;
            App.StartupTrace($"WebChatView replayed {_pendingMessages.Count} messages");
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
                    case "copy":
                        var text = root.TryGetProperty("text", out var textElement)
                            ? textElement.GetString()
                            : null;
                        if (!string.IsNullOrEmpty(text))
                        {
                            Clipboard.SetText(text);
                        }

                        break;
                    case "loadOlder":
                        OlderMessagesRequested?.Invoke(this, EventArgs.Empty);
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
                }
            }
        }
        catch (Exception ex)
        {
            App.StartupTrace($"WebChatView copy message failed: {ex.Message}");
        }
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
        try
        {
            await EnsureReadyAsync().ConfigureAwait(true);
            var documentReady = await WaitForDocumentReadyAsync().ConfigureAwait(true);
            if (!documentReady)
            {
                App.StartupTrace($"WebChatView ExecuteScript skipped: document not ready ({script.Length} chars)");
                return;
            }

            await ChatWebView.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"WebChatView ExecuteScript failed ({script.Length} chars): {ex.Message}");
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
            App.StartupTrace("WebChatView WaitForDocumentReady timed out after 5s");
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
        ExecuteScriptWhenReadyAsync("scrollToBottom(true);");

    public async Task PrependMessagesAsync(
        IReadOnlyList<ChatMessageViewModel> messages,
        bool showToolCalls,
        bool hasOlderMessages)
    {
        if (messages.Count == 0)
        {
            await SetOlderMessagesAvailableAsync(hasOlderMessages).ConfigureAwait(true);
            return;
        }

        try
        {
            await EnsureReadyAsync().ConfigureAwait(true);
            if (!await WaitForDocumentReadyAsync().ConfigureAwait(true))
            {
                return;
            }

            var snapshot = messages.ToArray();
            var json = await Task.Run(
                () => ChatEventSerializer.SerializePrependCommand(snapshot, showToolCalls, hasOlderMessages))
                .ConfigureAwait(true);
            ChatWebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            App.StartupTrace($"WebChatView prepend history failed: {ex.Message}");
        }
    }

    public async Task SetOlderMessagesAvailableAsync(bool hasOlderMessages)
    {
        try
        {
            await EnsureReadyAsync().ConfigureAwait(true);
            if (!await WaitForDocumentReadyAsync().ConfigureAwait(true))
            {
                return;
            }

            ChatWebView.CoreWebView2.PostWebMessageAsJson(
                ChatEventSerializer.SerializeHistoryAvailabilityCommand(hasOlderMessages));
        }
        catch (Exception ex)
        {
            App.StartupTrace($"WebChatView history availability failed: {ex.Message}");
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
