namespace Athlon.Agent.App.Services;

public interface ISpeechDictationService
{
    bool IsListening { get; }

    event EventHandler<string>? InterimText;

    event EventHandler<string>? FinalText;

    event EventHandler<string>? Error;

    Task StartAsync(string languageTag, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
