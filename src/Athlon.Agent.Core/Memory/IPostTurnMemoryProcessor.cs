namespace Athlon.Agent.Core.Memory;

public interface IPostTurnMemoryProcessor
{
    Task ProcessAsync(MemoryTurnContext context, CancellationToken cancellationToken = default);
}
