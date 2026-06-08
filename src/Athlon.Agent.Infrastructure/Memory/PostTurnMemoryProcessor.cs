using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Infrastructure.Memory;

public sealed class PostTurnMemoryProcessor(
    MemoryFlushService flushService,
    MemoryConsolidationService consolidationService,
    AppSettings settings,
    IAppLogger logger) : IPostTurnMemoryProcessor
{
    private readonly IAppLogger _logger = logger.ForContext("PostTurnMemoryProcessor");
    private DateTime _lastConsolidation = DateTime.MinValue;

    public async Task ProcessAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        if (!settings.Memory.Enabled)
            return;

        await flushService.FlushAsync(messages, cancellationToken);

        var now = DateTime.UtcNow;
        var gap = settings.Memory.ConsolidationMinGap;
        if (now - _lastConsolidation >= gap)
        {
            _lastConsolidation = now;
            await consolidationService.ConsolidateAsync(cancellationToken);
        }
    }
}
