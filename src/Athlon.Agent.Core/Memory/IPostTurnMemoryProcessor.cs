namespace Athlon.Agent.Core.Memory;

public interface IPostTurnMemoryProcessor
{
    Task ProcessAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);
}
