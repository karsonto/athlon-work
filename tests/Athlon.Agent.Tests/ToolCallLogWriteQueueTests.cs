using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class ToolCallLogWriteQueueTests
{
    [Fact]
    public async Task EnqueueAsync_FlushAsync_WritesInOrder()
    {
        var written = new List<string>();
        var writeLock = new SemaphoreSlim(1, 1);
        var queue = new ToolCallLogWriteQueue(
            async (sessionId, entry, _) =>
            {
                await writeLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    written.Add($"{sessionId}:{entry.ToolCallId}");
                    await Task.Delay(5).ConfigureAwait(false);
                }
                finally
                {
                    writeLock.Release();
                }
            },
            new NoOpLogger());

        await queue.EnqueueAsync("session-a", CreateEntry("call-1"));
        await queue.EnqueueAsync("session-a", CreateEntry("call-2"));
        await queue.EnqueueAsync("session-b", CreateEntry("call-3"));

        await queue.FlushAsync();

        Assert.Equal(3, written.Count);
        Assert.Equal("session-a:call-1", written[0]);
        Assert.Equal("session-a:call-2", written[1]);
        Assert.Equal("session-b:call-3", written[2]);
    }

    [Fact]
    public async Task EnqueueAsync_ReturnsBeforeWriteCompletes()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new ToolCallLogWriteQueue(
            async (_, _, _) =>
            {
                await tcs.Task.ConfigureAwait(false);
            },
            new NoOpLogger());

        await queue.EnqueueAsync("session-a", CreateEntry("call-1"));

        var flushTask = queue.FlushAsync();
        Assert.False(flushTask.IsCompleted);

        tcs.TrySetResult();
        await flushTask;
    }

    private static SessionToolCallLogEntry CreateEntry(string toolCallId) =>
        new(
            DateTimeOffset.UtcNow,
            toolCallId,
            "file_list",
            new Dictionary<string, string>(),
            true,
            "ok",
            "content",
            null,
            1);

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
