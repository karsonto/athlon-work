using NAudio.Wave;

namespace Athlon.Agent.App.Services.Audio;

/// <summary>
/// Plays streamed 16-bit mono PCM through the default output device. Audio arrives in small
/// network chunks, so it is appended to a <see cref="BufferedWaveProvider"/> and the output device
/// pulls from it continuously; callers only need <see cref="Enqueue"/> and <see cref="Stop"/>.
/// </summary>
public sealed class PcmAudioPlayer : IPcmAudioPlayer, IDisposable
{
    /// <summary>
    /// Playback buffer capacity. Large enough to absorb jitter across a whole utterance (60s of
    /// 24kHz/16-bit/mono is ~2.9 MB) without pre-buffering the entire clip before starting.
    /// </summary>
    private static readonly TimeSpan BufferDuration = TimeSpan.FromSeconds(60);

    /// <summary>Output latency. 150ms halves the risk of underrun without feeling laggy on stop.</summary>
    private const int DesiredLatencyMs = 150;

    private const int DefaultSampleRate = 24_000;

    private readonly object _gate = new();

    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private WaveFormat? _format;
    private volatile bool _playing;

    /// <summary>Sample rate the current playback was started with; 0 when idle.</summary>
    public int SampleRate { get; private set; }

    public bool IsPlaying => _playing;

    /// <summary>
    /// Starts (or restarts) playback for the given sample rate. Any previous playback is stopped
    /// first, so <see cref="Start"/> is safe to call whenever the first chunk arrives.
    /// </summary>
    public void Start(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            sampleRate = DefaultSampleRate;
        }

        lock (_gate)
        {
            StopLocked();
            StartLocked(sampleRate);
        }
    }

    /// <summary>Appends one PCM slice. Ignored when playback has not been started.</summary>
    public void Enqueue(byte[] pcm)
    {
        if (pcm.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _buffer?.AddSamples(pcm, 0, pcm.Length);
        }
    }

    /// <summary>
    /// Waits until everything enqueued so far has actually been heard. Called after the last chunk
    /// arrives so the caller can report "finished" at the right moment.
    /// </summary>
    public async Task WaitForDrainAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool drained;
            lock (_gate)
            {
                drained = _buffer is null || _buffer.BufferedBytes == 0;
            }

            if (drained)
            {
                break;
            }

            try
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        _playing = false;
    }

    /// <summary>Stops immediately, discarding any buffered audio. Safe to call while idle.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StartLocked(int sampleRate)
    {
        _format = new WaveFormat(sampleRate, 16, 1);
        _buffer = new BufferedWaveProvider(_format)
        {
            // Never signal end-of-stream early on a buffer underrun: audio arrives over the network,
            // so a gap must not be mistaken for "playback finished".
            ReadFully = true,
            BufferDuration = BufferDuration,
            DiscardOnBufferOverflow = true
        };

        var output = new WaveOutEvent { DesiredLatency = DesiredLatencyMs };
        output.PlaybackStopped += OnPlaybackStopped;
        output.Init(_buffer);
        output.Play();

        _output = output;
        SampleRate = sampleRate;
        _playing = true;
    }

    private void StopLocked()
    {
        _playing = false;
        SampleRate = 0;

        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                _output.Stop();
            }
            catch
            {
                // Stopping a device that already faulted must not mask the original error.
            }

            _output.Dispose();
            _output = null;
        }

        // ClearBuffer is the only supported way to drop pending audio; the provider is then
        // discarded with the device, so no stale bytes survive into the next utterance.
        _buffer?.ClearBuffer();
        _buffer = null;
        _format = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // With ReadFully = true the device does not stop on underrun; reaching here means Stop()
        // was called or the device failed. Either way playback is no longer active.
        _playing = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }
}
