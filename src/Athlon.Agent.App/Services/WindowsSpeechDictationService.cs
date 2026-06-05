using Windows.Devices.Enumeration;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace Athlon.Agent.App.Services;

public sealed class WindowsSpeechDictationService : ISpeechDictationService, IDisposable
{
    private SpeechRecognizer? _recognizer;
    private bool _isListening;

    public bool IsListening => _isListening;

    public event EventHandler<string>? InterimText;

    public event EventHandler<string>? FinalText;

    public event EventHandler<string>? Error;

    public async Task StartAsync(string languageTag, CancellationToken cancellationToken = default)
    {
        if (_isListening)
        {
            return;
        }

        var access = await DeviceAccessInformation
            .CreateFromDeviceClass(DeviceClass.AudioCapture)
            .RequestAccessAsync()
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (access != DeviceAccessStatus.Allowed)
        {
            RaiseError("请在 Windows 设置 → 隐私 → 麦克风中允许桌面应用访问麦克风。");
            return;
        }

        await StopInternalAsync(cancellationToken).ConfigureAwait(false);

        var recognizer = await CreateRecognizerAsync(languageTag, cancellationToken).ConfigureAwait(false);
        if (recognizer is null)
        {
            return;
        }

        _recognizer = recognizer;
        _recognizer.HypothesisGenerated += OnHypothesisGenerated;
        _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;
        _recognizer.ContinuousRecognitionSession.Completed += OnCompleted;

        try
        {
            await _recognizer.ContinuousRecognitionSession
                .StartAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            _isListening = true;
        }
        catch (Exception ex)
        {
            RaiseError($"无法启动语音识别：{ex.Message}");
            await StopInternalAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isListening && _recognizer is null)
        {
            return;
        }

        await StopInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        StopInternalAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    internal static async Task<string?> ResolveLanguageTagAsync(
        string? preferredLanguageTag,
        CancellationToken cancellationToken = default)
    {
        var candidates = BuildLanguageCandidates(preferredLanguageTag);
        var installed = SpeechRecognizer.SupportedTopicLanguages
            .Select(language => language.LanguageTag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (installed.Contains(candidate))
            {
                return candidate;
            }
        }

        var systemTag = SpeechRecognizer.SystemSpeechLanguage?.LanguageTag;
        if (!string.IsNullOrWhiteSpace(systemTag) && installed.Contains(systemTag))
        {
            return systemTag;
        }

        return installed.FirstOrDefault();
    }

    internal static async Task<SpeechRecognizer?> CreateRecognizerAsync(
        string languageTag,
        CancellationToken cancellationToken = default)
    {
        var resolvedTag = await ResolveLanguageTagAsync(languageTag, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resolvedTag))
        {
            return null;
        }

        var recognizer = new SpeechRecognizer(new Language(resolvedTag));

        recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(
            SpeechRecognitionScenario.Dictation,
            "dictation"));

        var compileResult = await recognizer.CompileConstraintsAsync()
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (compileResult.Status != SpeechRecognitionResultStatus.Success)
        {
            recognizer.Dispose();
            return null;
        }

        return recognizer;
    }

    private static IEnumerable<string> BuildLanguageCandidates(string? preferredLanguageTag)
    {
        if (!string.IsNullOrWhiteSpace(preferredLanguageTag))
        {
            yield return preferredLanguageTag.Trim();
        }

        yield return "zh-CN";
        yield return "zh-Hans-CN";
        yield return "en-US";
    }

    private void OnHypothesisGenerated(SpeechRecognizer sender, SpeechRecognitionHypothesisGeneratedEventArgs args)
    {
        InterimText?.Invoke(this, args.Hypothesis?.Text ?? string.Empty);
    }

    private void OnResultGenerated(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        if (args.Result.Status != SpeechRecognitionResultStatus.Success)
        {
            return;
        }

        var text = args.Result.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            FinalText?.Invoke(this, text);
        }
    }

    private void OnCompleted(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args)
    {
        if (_recognizer is null)
        {
            return;
        }

        var wasListening = _isListening;
        if (wasListening && args.Status != SpeechRecognitionResultStatus.Success)
        {
            RaiseError($"语音识别已结束：{args.Status}");
        }

        _ = StopInternalAsync(CancellationToken.None);
    }

    private async Task StopInternalAsync(CancellationToken cancellationToken)
    {
        var recognizer = _recognizer;
        _recognizer = null;
        _isListening = false;

        if (recognizer is null)
        {
            return;
        }

        recognizer.HypothesisGenerated -= OnHypothesisGenerated;
        recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
        recognizer.ContinuousRecognitionSession.Completed -= OnCompleted;

        try
        {
            await recognizer.ContinuousRecognitionSession
                .StopAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best effort shutdown.
        }

        try
        {
            await recognizer.ContinuousRecognitionSession
                .CancelAsync()
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best effort shutdown.
        }

        recognizer.Dispose();
    }

    private void RaiseError(string message) => Error?.Invoke(this, message);
}
