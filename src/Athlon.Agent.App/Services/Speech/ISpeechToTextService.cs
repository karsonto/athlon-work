namespace Athlon.Agent.App.Services.Speech;

public interface ISpeechToTextService : IDisposable
{
    bool IsAvailable { get; }

    bool IsListening { get; }

    event EventHandler<string>? PartialText;

    event EventHandler<string>? FinalText;

    event EventHandler<string>? Failed;

    event EventHandler? AvailabilityChanged;

    Task<bool> ProbeAvailabilityAsync(CancellationToken cancellationToken = default);

    Task StartListeningAsync(CancellationToken cancellationToken = default);

    Task StopListeningAsync(CancellationToken cancellationToken = default);
}
