namespace Athlon.Agent.Core;

/// <summary>
/// Writes conversation messages. Production uses an in-memory queue with periodic/shutdown flush;
/// tests and CLI keep the immediate disk writer.
/// </summary>
public interface IConversationTranscriptWriter
{
    Task AppendAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default);

    Task MarkSessionDirtyAsync(AgentSession session, CancellationToken cancellationToken = default);

    Task FlushSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task FlushAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class ImmediateConversationTranscriptWriter(IFileStorageService storage) : IConversationTranscriptWriter
{
    public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) =>
        storage.AppendConversationMessageAsync(sessionId, message, cancellationToken);

    public Task MarkSessionDirtyAsync(AgentSession session, CancellationToken cancellationToken = default) =>
        storage.SaveSessionAsync(session, cancellationToken);

    public Task FlushSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
