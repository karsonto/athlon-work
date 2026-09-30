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

/// <summary>WebView2 startup, navigation and page messages.</summary>
public partial class WebChatView
{
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
                    case "forkChat":
                        var forkMessageId = root.TryGetProperty("messageId", out var forkMessageIdElement)
                            ? forkMessageIdElement.GetString()
                            : null;
                        if (!string.IsNullOrEmpty(forkMessageId))
                        {
                            ForkChatRequested?.Invoke(this, forkMessageId);
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
}
