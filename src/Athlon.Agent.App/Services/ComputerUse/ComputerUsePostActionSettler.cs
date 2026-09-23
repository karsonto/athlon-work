namespace Athlon.Agent.App.Services.ComputerUse;

internal static class ComputerUsePostActionSettler
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(75);
    internal const int MaxSamples = 8;
    internal const int MinimumSamples = 4;
    private const int RequiredConsecutiveMatches = 2;

    /// <summary>
    /// Waits until the desktop signature stops changing. The budget is caller-supplied so that
    /// layout-neutral actions (typing, keys, scrolling) can settle in far fewer samples than
    /// actions that trigger navigation or reflow.
    /// </summary>
    internal static async Task<ComputerUseSettleResult> WaitForStableAsync(
        Func<CancellationToken, Task<ulong>> captureSignatureAsync,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        int minimumSamples = MinimumSamples,
        int maxSamples = MaxSamples,
        TimeSpan? sampleInterval = null,
        int requiredConsecutiveMatches = RequiredConsecutiveMatches)
    {
        ArgumentNullException.ThrowIfNull(captureSignatureAsync);
        delayAsync ??= static (delay, token) => Task.Delay(delay, token);

        // A stable verdict needs at least two identical samples, and never fewer samples than the
        // caller allows, so clamp defensively against a misconfigured settings file.
        maxSamples = Math.Max(2, maxSamples);
        minimumSamples = Math.Clamp(minimumSamples, 2, maxSamples);
        requiredConsecutiveMatches = Math.Clamp(requiredConsecutiveMatches, 1, maxSamples - 1);
        var interval = sampleInterval is { Ticks: > 0 } configured ? configured : SampleInterval;

        ulong? previous = null;
        var consecutiveMatches = 0;
        for (var sample = 1; sample <= maxSamples; sample++)
        {
            await delayAsync(interval, cancellationToken).ConfigureAwait(false);
            var current = await captureSignatureAsync(cancellationToken).ConfigureAwait(false);
            if (previous is { } prior && current == prior)
            {
                consecutiveMatches++;
                if (sample >= minimumSamples
                    && consecutiveMatches >= requiredConsecutiveMatches)
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

        return new ComputerUseSettleResult(IsStable: false, Samples: maxSamples);
    }
}

internal sealed record ComputerUseSettleResult(
    bool IsStable,
    int Samples);
