using System.Windows.Threading;
using Athlon.Agent.App.Services.Streaming;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.Tests;

[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class StreamingTokenBufferTests
{
    [Fact]
    public async Task StopFlushTimer_PreventsPendingStartFlushTimerFromCreatingTimer()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var buffer = new StreamingTokenBuffer(dispatcher, new SessionStreamingUiContext());
        var tickCount = 0;
        buffer.FlushTimerTick += (_, _) => Interlocked.Increment(ref tickCount);

        await dispatcher.InvokeAsync(() =>
        {
            buffer.ScheduleFlush(isDisplayed: true);
            buffer.StopFlushTimer();
        });

        // Let the queued StartFlushTimer BeginInvoke run.
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.False(buffer.HasActiveFlushTimer);
        Assert.Equal(0, Volatile.Read(ref tickCount));
    }

    [Fact]
    public async Task ScheduleFlush_WhenDisplayed_StartsTimer()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var buffer = new StreamingTokenBuffer(dispatcher, new SessionStreamingUiContext());

        await dispatcher.InvokeAsync(() => buffer.ScheduleFlush(isDisplayed: true));
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.True(buffer.HasActiveFlushTimer);

        await dispatcher.InvokeAsync(buffer.StopFlushTimer);
        Assert.False(buffer.HasActiveFlushTimer);
    }

    private static Task<Dispatcher> StartStaDispatcherAsync()
    {
        var tcs = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            tcs.SetResult(dispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
