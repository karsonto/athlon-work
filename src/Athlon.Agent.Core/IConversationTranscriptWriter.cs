namespace Athlon.Agent.Core;

/// <summary>
/// Writes conversation messages. Desktop sync-flushes at message boundaries;
/// tests and CLI use the immediate disk writer.
/// </summary>
public interface IConversationTranscriptWriter
{
    Task AppendAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="message"/>, replacing any pending row with the same Id.
    /// Used for mid-turn streaming checkpoints that must be overwritten by the final Persist.
    /// </summary>
    Task UpsertAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) =>
        AppendAsync(sessionId, message, cancellationToken);

    Task MarkSessionDirtyAsync(AgentSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces the UI display log and saves session.json.
    /// </summary>
    Task ReplaceDisplayAsync(
        AgentSession session,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

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

    public async Task ReplaceDisplayAsync(
        AgentSession session,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        await storage.ReplaceConversationDisplayAsync(session.Id, messages, cancellationToken).ConfigureAwait(false);
        await storage.SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public Task FlushSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
