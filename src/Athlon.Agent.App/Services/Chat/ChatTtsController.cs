using System.Text.Json;
using Athlon.Agent.App.Resources;
using Athlon.Agent.App.Services.Audio;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Audio;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.Chat;

/// <summary>
/// Orchestrates one read-aloud session: pulls streamed PCM from <see cref="ITtsClient"/> and feeds
/// it to the speakers, while reporting state (loading / playing / ended / error) back to the chat
/// timeline so the bubble button can reflect it.
/// </summary>
/// <remarks>
/// Synthesis lives here rather than in the WebView so the TTS API key never leaves the desktop
/// process. Only a small state machine crosses the WebView2 boundary.
/// </remarks>
public sealed class ChatTtsController(
    ITtsClient ttsClient,
    IPcmAudioPlayer audioPlayer,
    AppSettings settings,
    IAppLogger logger)
{
    private readonly IAppLogger _logger = logger.ForContext("ChatTts");

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Sends a command object to the chat page. Set by <see cref="Controls.WebChatView"/> once the
    /// document is ready, and replaced whenever the page is recreated.
    /// </summary>
    private Func<string, Task>? _postTarget;

    /// <summary>Message id currently being synthesized or played; null when idle.</summary>
    public string? ActiveMessageId { get; private set; }

    public bool IsActive => ActiveMessageId is not null;

    /// <summary>Whether read-aloud is enabled in settings; the play button is hidden when false.</summary>
    public bool Enabled => settings.Tts.Enabled;

    /// <summary>Registers the transport used to push state commands into the WebView.</summary>
    public void AttachPostTarget(Func<string, Task>? postTarget)
    {
        lock (_gate)
        {
            _postTarget = postTarget;
        }
    }

    /// <summary>
    /// Starts reading <paramref name="text"/> aloud, cancelling any playback already in flight.
    /// </summary>
    public async Task PlayAsync(string messageId, string text)
    {
        // One utterance at a time: a new request always wins over the previous one.
        CancelActive(reportState: true);

        if (!settings.Tts.Enabled)
        {
            await PostStateAsync(messageId, "error", Strings.Get("Chat_TtsNotConfigured")).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            await PostStateAsync(messageId, "ended", null).ConfigureAwait(false);
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _cts = cts;
            ActiveMessageId = messageId;
        }

        await PostStateAsync(messageId, "loading", null).ConfigureAwait(false);

        var started = false;
        try
        {
            await foreach (var chunk in ttsClient
                .StreamAsync(new TtsRequest(text), cts.Token)
                .ConfigureAwait(false))
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                if (!started)
                {
                    audioPlayer.Start(chunk.SampleRate);
                    started = true;
                    await PostStateAsync(messageId, "playing", null).ConfigureAwait(false);
                }

                audioPlayer.Enqueue(chunk.Pcm);
            }

            // The stream ended; wait for the buffer to drain so "ended" fires when audio stops,
            // not when the last byte was produced.
            await audioPlayer.WaitForDrainAsync(cts.Token).ConfigureAwait(false);

            if (!cts.IsCancellationRequested)
            {
                await PostStateAsync(messageId, "ended", null).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() already reported the terminal state.
        }
        catch (TtsClientException ex)
        {
            _logger.Warning("TTS 合成失败：{Message}", ex.Message);
            await PostStateAsync(messageId, "error", ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "TTS 朗读失败");
            await PostStateAsync(messageId, "error", ex.Message).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                    ActiveMessageId = null;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Stops any in-flight synthesis and playback. Called by the stop button, and by the WebView on
    /// unload / session switch so a stale utterance cannot keep talking over a new session.
    /// </summary>
    public void Stop()
    {
        CancelActive(reportState: true);
    }

    private void CancelActive(bool reportState)
    {
        string? messageId = null;
        CancellationTokenSource? cts = null;

        lock (_gate)
        {
            if (_cts is not null)
            {
                cts = _cts;
                _cts = null;
                messageId = ActiveMessageId;
                ActiveMessageId = null;
            }
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced with the completion path; nothing left to cancel.
        }

        audioPlayer.Stop();

        if (reportState && messageId is not null)
        {
            // Fire-and-forget: callers of Stop() are UI paths that must not block on the WebView.
            _ = PostStateAsync(messageId, "ended", null);
        }
    }

    private async Task PostStateAsync(string messageId, string state, string? error)
    {
        Func<string, Task>? target;
        lock (_gate)
        {
            target = _postTarget;
        }

        if (target is null)
        {
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["command"] = "ttsState",
            ["messageId"] = messageId,
            ["state"] = state
        };
        if (!string.IsNullOrWhiteSpace(error))
        {
            payload["error"] = error;
        }

        try
        {
            await target(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The WebView can be torn down mid-flight; a failed status push must not surface as an error.
            _logger.Debug("TTS 状态回推失败：{Message}", ex.Message);
        }
    }
}
