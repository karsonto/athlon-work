using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;

namespace Athlon.Agent.Tests;

internal sealed class NoOpPostTurnMemoryProcessor : IPostTurnMemoryProcessor
{
    public Task ProcessAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
