namespace Athlon.Agent.App.Services.ComputerUse;

internal static class ComputerUsePostActionSettler
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(75);
    internal const int MaxSamples = 8;
    internal const int MinimumSamples = 4;
    private const int RequiredConsecutiveMatches = 2;

    internal static async Task<ComputerUseSettleResult> WaitForStableAsync(
        Func<CancellationToken, Task<ulong>> captureSignatureAsync,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(captureSignatureAsync);
        delayAsync ??= static (delay, token) => Task.Delay(delay, token);

        ulong? previous = null;
        var consecutiveMatches = 0;
        for (var sample = 1; sample <= MaxSamples; sample++)
        {
            await delayAsync(SampleInterval, cancellationToken).ConfigureAwait(false);
            var current = await captureSignatureAsync(cancellationToken).ConfigureAwait(false);
            if (previous is { } prior && current == prior)
            {
                consecutiveMatches++;
                if (sample >= MinimumSamples
                    && consecutiveMatches >= RequiredConsecutiveMatches)
                {
                    return new ComputerUseSettleResult(IsStable: true, Samples: sample);
                }
            }
            else
            {
                consecutiveMatches = 0;
            }

            previous = current;
        }

        return new ComputerUseSettleResult(IsStable: false, Samples: MaxSamples);
    }
}

internal sealed record ComputerUseSettleResult(
    bool IsStable,
    int Samples);
