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

/// <summary>Read-aloud transport, scrolling and older-history paging.</summary>
public partial class WebChatView
{
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
