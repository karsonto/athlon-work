using System.Text;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace Athlon.Agent.App.Services.Speech;

/// <summary>
/// Windows.Media.SpeechRecognition push-to-talk backend.
/// Probe failures leave <see cref="IsAvailable"/> false so the UI can hide the mic.
/// </summary>
public sealed class WindowsSpeechToTextService : ISpeechToTextService
{
    private readonly object _gate = new();
    private readonly StringBuilder _sessionText = new();

    private SpeechRecognizer? _recognizer;
    private bool _probed;
    private bool _isAvailable;
    private bool _isListening;
    private bool _isCompiled;
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

    public async Task<bool> ProbeAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_probed)
            {
                return _isAvailable;
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                SetAvailability(available: false, probed: true);
                return false;
            }

            var language = ResolveLanguage();
            if (language is null)
            {
                SetAvailability(available: false, probed: true);
                return false;
            }

            var recognizer = new SpeechRecognizer(language);
            recognizer.Constraints.Clear();
            recognizer.Constraints.Add(
                new SpeechRecognitionTopicConstraint(
                    SpeechRecognitionScenario.Dictation,
                    "athlonDictation"));

            var compileResult = await recognizer.CompileConstraintsAsync();
            cancellationToken.ThrowIfCancellationRequested();

            if (compileResult.Status != SpeechRecognitionResultStatus.Success)
            {
                recognizer.Dispose();
                SetAvailability(available: false, probed: true);
                return false;
            }

            recognizer.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromMinutes(2);
            recognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;
            recognizer.ContinuousRecognitionSession.Completed += OnSessionCompleted;

            lock (_gate)
            {
                _recognizer = recognizer;
                _isCompiled = true;
                _probed = true;
                _isAvailable = true;
            }

            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            DisposeRecognizerUnsafe();
            SetAvailability(available: false, probed: true);
            return false;
        }
    }

    public async Task StartListeningAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!await ProbeAvailabilityAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        SpeechRecognizer recognizer;
        lock (_gate)
        {
            if (_isListening || _recognizer is null)
            {
                return;
            }

            recognizer = _recognizer;
            _sessionText.Clear();
            _isListening = true;
        }

        try
        {
            if (!_isCompiled)
            {
                var compileResult = await recognizer.CompileConstraintsAsync();
                if (compileResult.Status != SpeechRecognitionResultStatus.Success)
                {
                    lock (_gate)
                    {
                        _isListening = false;
                    }

                    Failed?.Invoke(this, compileResult.Status.ToString());
                    return;
                }

                _isCompiled = true;
            }

            if (recognizer.State != SpeechRecognizerState.Idle)
            {
                await recognizer.ContinuousRecognitionSession.CancelAsync();
            }

            await recognizer.ContinuousRecognitionSession.StartAsync();
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

        SpeechRecognizer? recognizer;
        string finalized;
        lock (_gate)
        {
            if (!_isListening || _recognizer is null)
            {
                return;
            }

            recognizer = _recognizer;
        }

        try
        {
            if (recognizer.State != SpeechRecognizerState.Idle)
            {
                await recognizer.ContinuousRecognitionSession.StopAsync();
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _isListening = false;
                finalized = _sessionText.ToString().Trim();
                _sessionText.Clear();
            }
        }

        if (!string.IsNullOrWhiteSpace(finalized))
        {
            FinalText?.Invoke(this, finalized);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        SpeechRecognizer? recognizer;
        lock (_gate)
        {
            recognizer = _recognizer;
            _recognizer = null;
            _isListening = false;
            _isAvailable = false;
        }

        if (recognizer is null)
        {
            return;
        }

        try
        {
            recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
            recognizer.ContinuousRecognitionSession.Completed -= OnSessionCompleted;
        }
        catch
        {
            // ignore teardown races
        }

        try
        {
            recognizer.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private void OnResultGenerated(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        var result = args.Result;
        var text = result?.Text?.Trim();
        if (result is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Reject very low confidence fragments.
        if (result.Confidence is SpeechRecognitionConfidence.Rejected)
        {
            return;
        }

        lock (_gate)
        {
            if (!_isListening)
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

    private void OnSessionCompleted(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionCompletedEventArgs args)
    {
        if (args.Status is SpeechRecognitionResultStatus.Success
            or SpeechRecognitionResultStatus.UserCanceled)
        {
            return;
        }

        lock (_gate)
        {
            if (!_isListening)
            {
                return;
            }

            _isListening = false;
        }

        Failed?.Invoke(this, args.Status.ToString());
    }

    private static Language? ResolveLanguage()
    {
        try
        {
            var supported = SpeechRecognizer.SupportedTopicLanguages;
            if (supported is null || supported.Count == 0)
            {
                return null;
            }

            foreach (var candidate in supported)
            {
                if (string.Equals(candidate.LanguageTag, "zh-CN", StringComparison.OrdinalIgnoreCase)
                    || candidate.LanguageTag.StartsWith("zh-CN-", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            var system = SpeechRecognizer.SystemSpeechLanguage;
            if (system is not null
                && supported.Any(l => string.Equals(l.LanguageTag, system.LanguageTag, StringComparison.OrdinalIgnoreCase)))
            {
                return system;
            }

            return supported[0];
        }
        catch
        {
            return null;
        }
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

    private void DisposeRecognizerUnsafe()
    {
        SpeechRecognizer? recognizer;
        lock (_gate)
        {
            recognizer = _recognizer;
            _recognizer = null;
            _isCompiled = false;
        }

        if (recognizer is null)
        {
            return;
        }

        try
        {
            recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
            recognizer.ContinuousRecognitionSession.Completed -= OnSessionCompleted;
            recognizer.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
