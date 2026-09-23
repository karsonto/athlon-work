using Athlon.Agent.App.Services.ComputerUse;

namespace Athlon.Agent.Tests;

public sealed class ComputerUsePostActionSettlerTests
{
    [Fact]
    public async Task WaitForStableAsync_RequiresMinimumStableWindow()
    {
        var signatures = new Queue<ulong>([42, 42, 42, 42, 99]);

        var result = await ComputerUsePostActionSettler.WaitForStableAsync(
            _ => Task.FromResult(signatures.Dequeue()),
            CancellationToken.None,
            static (_, _) => Task.CompletedTask);

        Assert.True(result.IsStable);
        Assert.Equal(ComputerUsePostActionSettler.MinimumSamples, result.Samples);
        Assert.Single(signatures);
    }

    [Fact]
    public async Task WaitForStableAsync_RestartsStableWindowAfterChange()
    {
        var signatures = new Queue<ulong>([1, 1, 2, 2, 2]);

        var result = await ComputerUsePostActionSettler.WaitForStableAsync(
            _ => Task.FromResult(signatures.Dequeue()),
            CancellationToken.None,
            static (_, _) => Task.CompletedTask);

        Assert.True(result.IsStable);
        Assert.Equal(5, result.Samples);
        Assert.Empty(signatures);
    }

    [Fact]
    public async Task WaitForStableAsync_StopsAtBoundWhenDesktopKeepsChanging()
    {
        ulong signature = 0;

        var result = await ComputerUsePostActionSettler.WaitForStableAsync(
            _ => Task.FromResult(++signature),
            CancellationToken.None,
            static (_, _) => Task.CompletedTask);

        Assert.False(result.IsStable);
        Assert.Equal(ComputerUsePostActionSettler.MaxSamples, result.Samples);
        Assert.Equal((ulong)ComputerUsePostActionSettler.MaxSamples, signature);
    }

    [Fact]
    public async Task WaitForStableAsync_ObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ComputerUsePostActionSettler.WaitForStableAsync(
                _ => Task.FromResult(1UL),
                cancellation.Token,
                static (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }));
    }

    [Fact]
    public async Task WaitForStableAsync_PropagatesSignatureProbeFailure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ComputerUsePostActionSettler.WaitForStableAsync(
                _ => Task.FromException<ulong>(new InvalidOperationException("no pixels")),
                CancellationToken.None,
                static (_, _) => Task.CompletedTask));
    }

    // ---- Configurable budget (Phase 2.1) ---------------------------------

    [Fact]
    public async Task WaitForStableAsync_LayoutNeutralBudgetSettlesInTwoSamples()
    {
        // Typing and key presses do not reflow the window, so they keep the short budget and return
        // well before the conservative default.
        var started = DateTime.UtcNow;

        var result = await ComputerUsePostActionSettler.WaitForStableAsync(
            _ => Task.FromResult(7UL),
            CancellationToken.None,
            static (_, _) => Task.CompletedTask,
            minimumSamples: 2,
            maxSamples: 8);

        Assert.True(result.IsStable);
        Assert.Equal(2, result.Samples);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WaitForStableAsync_RespectsConfiguredMaxSamples()
    {
        ulong signature = 0;

        var result = await ComputerUsePostActionSettler.WaitForStableAsync(
            _ => Task.FromResult(++signature),
            CancellationToken.None,
            static (_, _) => Task.CompletedTask,
            minimumSamples: 4,
            maxSamples: 5);

        Assert.False(result.IsStable);
        Assert.Equal(5, result.Samples);
        Assert.Equal(5UL, signature);
    }

    [Fact]
    public async Task WaitForStableAsync_ClampsMisconfiguredBudget()
    {
        // A settings file could set minimumSamples above maxSamples; the loop must still terminate
        // with at least two samples so a stable desktop can be detected.
        var result = await ComputerUsePostActionSettler.WaitForStableAsync(
            _ => Task.FromResult(3UL),
            CancellationToken.None,
            static (_, _) => Task.CompletedTask,
            minimumSamples: 99,
            maxSamples: 0);

        Assert.True(result.IsStable);
        Assert.Equal(2, result.Samples);
    }

    [Fact]
    public async Task WaitForStableAsync_UsesConfiguredSampleInterval()
    {
        TimeSpan? observed = null;

        await ComputerUsePostActionSettler.WaitForStableAsync(
            _ => Task.FromResult(1UL),
            CancellationToken.None,
            (delay, _) =>
            {
                observed ??= delay;
                return Task.CompletedTask;
            },
            minimumSamples: 2,
            maxSamples: 4,
            sampleInterval: TimeSpan.FromMilliseconds(12));

        Assert.Equal(TimeSpan.FromMilliseconds(12), observed);
    }
}
