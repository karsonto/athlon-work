using Athlon.Agent.Core.Threading;

namespace Athlon.Agent.Tests;

public sealed class SyncOverAsyncTests
{
    [Fact]
    public void Run_Completes_WhenAwaitedWorkUsesConfigureAwaitTrue()
    {
        // Regression: prompt contributors used to call GetAwaiter().GetResult() directly.
        // Under a UI SynchronizationContext that deadlocks; Task.Run detaches safely.
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            var result = SyncOverAsync.Run(async () =>
            {
                await Task.Delay(5).ConfigureAwait(true);
                return 42;
            });

            Assert.Equal(42, result);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void Run_Void_Completes()
    {
        var touched = false;
        SyncOverAsync.Run(async () =>
        {
            await Task.Yield();
            touched = true;
        });
        Assert.True(touched);
    }
}
