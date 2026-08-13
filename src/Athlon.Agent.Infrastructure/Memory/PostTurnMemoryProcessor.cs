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

    public async Task ProcessAsync(MemoryTurnContext context, CancellationToken cancellationToken = default)
    {
        await flushService.FlushAsync(context, cancellationToken);

        var now = DateTime.UtcNow;
        var gap = settings.Memory.ConsolidationMinGap;
        if (now - _lastConsolidation >= gap)
        {
            _lastConsolidation = now;
            await consolidationService.ConsolidateAsync(cancellationToken);
        }
    }
}
