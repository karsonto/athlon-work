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
}
