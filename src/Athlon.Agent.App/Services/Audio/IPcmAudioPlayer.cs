namespace Athlon.Agent.App.Services.Audio;

/// <summary>
/// Output device abstraction for streamed PCM playback. Exists so the orchestration logic can be
/// tested without opening a real audio device, which is unavailable in build agents.
/// </summary>
public interface IPcmAudioPlayer
{
    /// <summary>Starts (or restarts) playback for the given sample rate.</summary>
    void Start(int sampleRate);

    /// <summary>Appends one PCM slice.</summary>
    void Enqueue(byte[] pcm);

    /// <summary>Completes once every enqueued byte has been heard.</summary>
    Task WaitForDrainAsync(CancellationToken cancellationToken);

    /// <summary>Stops immediately and discards buffered audio. Safe to call while idle.</summary>
    void Stop();
}
