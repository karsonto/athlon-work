namespace Athlon.Agent.Core;

/// <summary>
/// Writes conversation messages. Production uses an in-memory queue with periodic/shutdown flush;
/// tests and CLI keep the immediate disk writer.
/// </summary>
public interface IConversationTranscriptWriter
{
    Task AppendAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues <paramref name="message"/> for flush, replacing any pending row with the same Id.
    /// Used for mid-turn streaming checkpoints that must be overwritten by the final Persist.
    /// </summary>
    Task UpsertAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) =>
        AppendAsync(sessionId, message, cancellationToken);

    Task MarkSessionDirtyAsync(AgentSession session, CancellationToken cancellationToken = default);

    Task FlushSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task FlushAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class ImmediateConversationTranscriptWriter(IFileStorageService storage) : IConversationTranscriptWriter
{
    public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) =>
        storage.AppendConversationMessageAsync(sessionId, message, cancellationToken);

    public Task UpsertAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) =>
        storage.AppendConversationMessageAsync(sessionId, message, cancellationToken);

    public Task MarkSessionDirtyAsync(AgentSession session, CancellationToken cancellationToken = default) =>
        storage.SaveSessionAsync(session, cancellationToken);

    public Task FlushSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
