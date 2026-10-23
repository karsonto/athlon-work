using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core.Storage;

public interface ISessionArtifactRepository
{
    Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionTranscriptInfo>> ListSessionTranscriptsAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<string?> ReadSessionTranscriptAsync(
        string sessionId,
        string fileName,
        int offsetChars,
        int limitChars,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionTranscriptMatch>> SearchSessionTranscriptsAsync(
        string sessionId,
        string query,
        int limit,
        int previewChars,
        CancellationToken cancellationToken = default);

    Task<string> SaveEvictedToolResultAsync(
        string sessionId,
        string toolCallId,
        string content,
        CancellationToken cancellationToken = default);

    Task<string?> TryReadEvictedToolResultAsync(
        string sessionId,
        string toolCallId,
        CancellationToken cancellationToken = default);

    Task<string> ReadHandoffNoteAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<HandoffNoteAppendResult> TryAppendHandoffNoteAsync(
        string sessionId,
        string text,
        CancellationToken cancellationToken = default);
}
