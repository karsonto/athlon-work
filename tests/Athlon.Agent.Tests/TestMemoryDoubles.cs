using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Tests;

internal sealed class NoOpPostTurnMemoryProcessor : IPostTurnMemoryProcessor
{
    public Task ProcessAsync(MemoryTurnContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
