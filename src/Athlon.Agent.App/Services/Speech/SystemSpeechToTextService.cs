using System.Globalization;
using System.Speech.Recognition;
using System.Text;

namespace Athlon.Agent.App.Services.Speech;

/// <summary>
/// Desktop speech-to-text via System.Speech (in-process). Used as fallback when WinRT produces no text.
/// </summary>
public sealed class SystemSpeechToTextService : ISpeechToTextService
{
    private readonly object _gate = new();
    private readonly StringBuilder _sessionText = new();

    private SpeechRecognitionEngine? _engine;
    private TaskCompletionSource? _stopCompletion;
    private bool _probed;
    private bool _isAvailable;
    private bool _isListening;
    private bool _isStopping;
    private bool _disposed;

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _isAvailable;
            }
        }
    }

    public bool IsListening
    {
        get
        {
            lock (_gate)
            {
                return _isListening;
            }
        }
    }

    public event EventHandler<string>? PartialText;

    public event EventHandler<string>? FinalText;

    public event EventHandler<string>? Failed;

    public event EventHandler? AvailabilityChanged;

    public Task<bool> ProbeAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_probed)
            {
                return Task.FromResult(_isAvailable);
            }
        }

        try
        {
            var engine = CreateEngine();
            if (engine is null)
            {
                SetAvailability(available: false, probed: true);
                return Task.FromResult(false);
            }

            lock (_gate)
            {
                _engine = engine;
                _probed = true;
                _isAvailable = true;
            }

            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            DisposeEngineUnsafe();
            SetAvailability(available: false, probed: true);
            Failed?.Invoke(this, ex.Message);
            return Task.FromResult(false);
        }
    }

    public async Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!await ProbeAvailabilityAsync(cancellationToken).ConfigureAwait(true))
        {
            lock (_gate)
            {
                _probed = false;
            }

            if (!await ProbeAvailabilityAsync(cancellationToken).ConfigureAwait(true))
            {
                Failed?.Invoke(this, "Speech recognition is not available. Install a Windows speech language pack.");
                return;
            }
        }

        SpeechRecognitionEngine engine;
        lock (_gate)
        {
            if (_isListening || _engine is null)
            {
                return;
            }

            engine = _engine;
            _sessionText.Clear();
            _isListening = true;
            _isStopping = false;
            _stopCompletion = null;
        }

        try
        {
            engine.RecognizeAsyncCancel();
            engine.SetInputToDefaultAudioDevice();
            engine.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _isListening = false;
            }

            Failed?.Invoke(this, ex.Message);
        }
    }

    public async Task StopListeningAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SpeechRecognitionEngine? engine;
        TaskCompletionSource stopCompletion;
        lock (_gate)
        {
            if (!_isListening || _engine is null)
            {
                return;
            }

            engine = _engine;
            _isStopping = true;
            stopCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopCompletion = stopCompletion;
        }

        try
        {
            engine.RecognizeAsyncStop();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await stopCompletion.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Timed out — still emit whatever we have.
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex.Message);
        }
        finally
        {
            string finalized;
            lock (_gate)
            {
                _isListening = false;
                _isStopping = false;
                _stopCompletion = null;
                finalized = _sessionText.ToString().Trim();
                _sessionText.Clear();
            }

            if (!string.IsNullOrWhiteSpace(finalized))
            {
                FinalText?.Invoke(this, finalized);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeEngineUnsafe();
        lock (_gate)
        {
            _isListening = false;
            _isAvailable = false;
            _stopCompletion?.TrySetResult();
            _stopCompletion = null;
        }
    }

    private SpeechRecognitionEngine? CreateEngine()
    {
        var culture = ResolveCulture();
        if (culture is null)
        {
            return null;
        }

        var engine = new SpeechRecognitionEngine(culture);
        engine.BabbleTimeout = TimeSpan.FromSeconds(3);
        engine.InitialSilenceTimeout = TimeSpan.FromSeconds(6);
        engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(750);
        engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(750);
        engine.LoadGrammar(new DictationGrammar());
        engine.SpeechRecognized += OnSpeechRecognized;
        engine.SpeechHypothesized += OnSpeechHypothesized;
        engine.RecognizeCompleted += OnRecognizeCompleted;
        return engine;
    }

    private static CultureInfo? ResolveCulture()
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        if (installed.Count == 0)
        {
            return null;
        }

        foreach (var info in installed)
        {
            if (string.Equals(info.Culture.Name, "zh-CN", StringComparison.OrdinalIgnoreCase)
                || info.Culture.Name.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
            {
                return info.Culture;
            }
        }

        return installed[0].Culture;
    }

    private void AppendRecognized(string text)
    {
        lock (_gate)
        {
            if (!_isListening && !_isStopping)
            {
                return;
            }

            if (_sessionText.Length > 0)
            {
                _sessionText.Append(' ');
            }

            _sessionText.Append(text);
            PartialText?.Invoke(this, _sessionText.ToString());
        }
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var text = e.Result?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            AppendRecognized(text);
        }
    }

    private void OnSpeechHypothesized(object? sender, SpeechHypothesizedEventArgs e)
    {
        var text = e.Result?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string preview;
        lock (_gate)
        {
            if (!_isListening && !_isStopping)
            {
                return;
            }

            preview = _sessionText.Length == 0 ? text : _sessionText + " " + text;
        }

        PartialText?.Invoke(this, preview);
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (e.Error is not null)
        {
            Failed?.Invoke(this, e.Error.Message);
        }
        else if (!e.Cancelled && e.Result?.Text is { Length: > 0 } text)
        {
            var trimmed = text.Trim();
            lock (_gate)
            {
                var current = _sessionText.ToString();
                if (!current.Contains(trimmed, StringComparison.Ordinal))
                {
                    if (_sessionText.Length > 0)
                    {
                        _sessionText.Append(' ');
                    }

                    _sessionText.Append(trimmed);
                }
            }
        }

        TaskCompletionSource? stopCompletion;
        lock (_gate)
        {
            stopCompletion = _stopCompletion;
        }

        stopCompletion?.TrySetResult();
    }

    private void SetAvailability(bool available, bool probed)
    {
        bool changed;
        lock (_gate)
        {
            changed = _isAvailable != available || !_probed;
            _isAvailable = available;
            _probed = probed;
        }

        if (changed)
        {
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DisposeEngineUnsafe()
    {
        SpeechRecognitionEngine? engine;
        lock (_gate)
        {
            engine = _engine;
            _engine = null;
        }

        if (engine is null)
        {
            return;
        }

        try
        {
            engine.SpeechRecognized -= OnSpeechRecognized;
            engine.SpeechHypothesized -= OnSpeechHypothesized;
            engine.RecognizeCompleted -= OnRecognizeCompleted;
            engine.RecognizeAsyncCancel();
            engine.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
