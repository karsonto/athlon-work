using System.Threading.Channels;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal sealed class ToolCallLogWriteQueue
{
    private readonly Channel<ToolCallLogWriteItem> _channel;
    private readonly Func<string, SessionToolCallLogEntry, CancellationToken, Task> _writeCore;
    private readonly IAppLogger _logger;
    private int _pendingWrites;

    public ToolCallLogWriteQueue(
        Func<string, SessionToolCallLogEntry, CancellationToken, Task> writeCore,
        IAppLogger logger)
    {
        _writeCore = writeCore;
        _logger = logger.ForContext("ToolCallLogWriteQueue");
        _channel = Channel.CreateUnbounded<ToolCallLogWriteItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _ = Task.Run(ProcessQueueAsync);
    }

    public async Task EnqueueAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        Interlocked.Increment(ref _pendingWrites);
        try
        {
            await _channel.Writer.WriteAsync(new ToolCallLogWriteItem(sessionId, entry), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _pendingWrites);
            throw;
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref _pendingWrites) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await _writeCore(item.SessionId, item.Entry, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        "Failed to write tool call log for session {SessionId}, tool {ToolName}: {Error}",
                        item.SessionId,
                        item.Entry.ToolName,
                        ex.Message);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingWrites);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Tool call log write queue worker failed");
        }
    }

    private sealed record ToolCallLogWriteItem(string SessionId, SessionToolCallLogEntry Entry);
}
