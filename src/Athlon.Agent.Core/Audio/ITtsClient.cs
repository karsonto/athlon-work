namespace Athlon.Agent.Core.Audio;

/// <summary>A single synthesis request. Null values fall back to <c>TtsSettings</c>.</summary>
public sealed record TtsRequest(
    string Text,
    string? Voice = null,
    double? Speed = null,
    string? Instructions = null);

/// <summary>
/// One PCM slice of synthesized audio. The same <see cref="SampleRate"/> is repeated on every
/// chunk so a consumer can start the output device from the first chunk alone.
/// </summary>
public sealed record TtsAudioChunk(byte[] Pcm, int SampleRate);

/// <summary>Result of probing the TTS service (<c>GET /health</c>).</summary>
public sealed record TtsProbeResult(
    string? Model,
    int SampleRate,
    bool Ffmpeg,
    IReadOnlyList<string> Voices,
    IReadOnlyList<string> Formats);

/// <summary>
/// OpenAI-compatible text-to-speech client. Synthesis runs in the desktop process so the API key
/// never reaches the WebView; the chat timeline only sends play/stop commands.
/// </summary>
public interface ITtsClient
{
    /// <summary>
    /// Streams raw 16-bit mono PCM as it is synthesized. Enumeration finishes when the whole
    /// utterance has been produced.
    /// </summary>
    IAsyncEnumerable<TtsAudioChunk> StreamAsync(TtsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Probes the configured service for readiness, sample rate, and voice list.</summary>
    Task<TtsProbeResult> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when the TTS call fails in a way worth showing to the user (auth, connection, or a
/// server-side error). Kept separate from transport exceptions so the UI can render the message.
/// </summary>
public sealed class TtsClientException : Exception
{
    public TtsClientException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
