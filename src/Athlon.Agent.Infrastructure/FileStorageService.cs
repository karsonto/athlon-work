using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Core.Storage;
using Athlon.Agent.Infrastructure.Storage;
using Athlon.Agent.Infrastructure.Storage.Repositories;

namespace Athlon.Agent.Infrastructure;

public sealed class FileStorageService : IFileStorageService
{
    private readonly IAppPathProvider _paths;
    private readonly ISessionStorageRepository _sessionStorage;
    private readonly IConversationDisplayRepository _conversationDisplay;
    private readonly ISessionArtifactRepository _sessionArtifacts;
    private readonly IAppSettingsRepository _appSettings;
    private readonly IAttemptEventRecorder _attemptEvents;

    public FileStorageService(
        IAppPathProvider paths,
        ISessionStorageRepository sessionStorage,
        IConversationDisplayRepository conversationDisplay,
        ISessionArtifactRepository sessionArtifacts,
        IAppSettingsRepository appSettings,
        IAttemptEventRecorder attemptEvents)
    {
        _paths = paths;
        _sessionStorage = sessionStorage;
        _conversationDisplay = conversationDisplay;
        _sessionArtifacts = sessionArtifacts;
        _appSettings = appSettings;
        _attemptEvents = attemptEvents;
    }

    /// <summary>
    /// Compatibility constructor for tests and direct instantiation without DI.
    /// Internal so the container does not treat it as a second activation candidate.
    /// </summary>
    internal FileStorageService(
        IAppLogger logger,
        IAppPathProvider paths,
        IJsonFileStore jsonFileStore,
        IAgentRunContextAccessor runContextAccessor,
        IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null)
        : this(CreateCompatDependencies(logger, paths, jsonFileStore, runContextAccessor, runtimeDiagnosticEventSink))
    {
    }

    private static (IAppPathProvider Paths, ISessionStorageRepository SessionStorage, IConversationDisplayRepository ConversationDisplay, ISessionArtifactRepository SessionArtifacts, IAppSettingsRepository AppSettings, IAttemptEventRecorder AttemptEvents) CreateCompatDependencies(
        IAppLogger logger,
        IAppPathProvider paths,
        IJsonFileStore jsonFileStore,
        IAgentRunContextAccessor runContextAccessor,
        IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink)
    {
        var sessionDirectories = new SessionDirectoryService(paths, runContextAccessor);
        var indexCoordinator = new SessionIndexCoordinator(paths, jsonFileStore, runContextAccessor);
        return (
            paths,
            new SessionStorageFileRepository(
                logger,
                paths,
                jsonFileStore,
                runContextAccessor,
                sessionDirectories,
                indexCoordinator,
                runtimeDiagnosticEventSink),
            new ConversationDisplayFileRepository(jsonFileStore, sessionDirectories),
            new SessionArtifactFileRepository(sessionDirectories),
            new AppSettingsFileRepository(paths, jsonFileStore),
            new AttemptEventRecorder());
    }

    private FileStorageService(
        (IAppPathProvider Paths, ISessionStorageRepository SessionStorage, IConversationDisplayRepository ConversationDisplay, ISessionArtifactRepository SessionArtifacts, IAppSettingsRepository AppSettings, IAttemptEventRecorder AttemptEvents) deps)
        : this(
            deps.Paths,
            deps.SessionStorage,
            deps.ConversationDisplay,
            deps.SessionArtifacts,
            deps.AppSettings,
            deps.AttemptEvents)
    {
    }

    public string RootPath => _paths.RootPath;

    public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default) =>
        _sessionStorage.SaveSessionAsync(session, cancellationToken);

    public Task SaveSessionOffThreadAsync(AgentSession session, CancellationToken cancellationToken = default) =>
        _sessionStorage.SaveSessionOffThreadAsync(session, cancellationToken);

    public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _sessionStorage.LoadSessionAsync(sessionId, cancellationToken);

    public Task<SessionIndexEntry?> LoadSessionIndexEntryAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _sessionStorage.LoadSessionIndexEntryAsync(sessionId, cancellationToken);

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _sessionStorage.DeleteSessionAsync(sessionId, cancellationToken);

    public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) =>
        _sessionStorage.SaveContextSummaryAsync(summary, cancellationToken);

    public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        _sessionStorage.ListSessionsAsync(cancellationToken);

    public Task<ChatMessage?> TryLoadConversationMessageAsync(
        string sessionId,
        string messageId,
        CancellationToken cancellationToken = default) =>
        _conversationDisplay.TryLoadConversationMessageAsync(sessionId, messageId, cancellationToken);

    public Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) =>
        _conversationDisplay.AppendConversationMessageAsync(sessionId, message, cancellationToken);

    public Task ReplaceConversationDisplayAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        _conversationDisplay.ReplaceConversationDisplayAsync(sessionId, messages, cancellationToken);

    public Task ReplaceConversationDisplayOffThreadAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        _conversationDisplay.ReplaceConversationDisplayOffThreadAsync(sessionId, messages, cancellationToken);

    public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        _conversationDisplay.LoadConversationDisplayAsync(sessionId, cancellationToken);

    public Task<ConversationDisplayPage> LoadConversationDisplayPageAsync(
        string sessionId,
        ConversationDisplayCursor? cursor = null,
        int pageSize = ConversationDisplayLimits.PageSize,
        CancellationToken cancellationToken = default) =>
        _conversationDisplay.LoadConversationDisplayPageAsync(sessionId, cursor, pageSize, cancellationToken);

    public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _conversationDisplay.ClearConversationDisplayAsync(sessionId, cancellationToken);

    public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) =>
        _sessionArtifacts.SaveTranscriptAsync(sessionId, messages, cancellationToken);

    public Task<string> SaveEvictedToolResultAsync(
        string sessionId,
        string toolCallId,
        string content,
        CancellationToken cancellationToken = default) =>
        _sessionArtifacts.SaveEvictedToolResultAsync(sessionId, toolCallId, content, cancellationToken);

    public Task<string?> TryReadEvictedToolResultAsync(
        string sessionId,
        string toolCallId,
        CancellationToken cancellationToken = default) =>
        _sessionArtifacts.TryReadEvictedToolResultAsync(sessionId, toolCallId, cancellationToken);

    public Task<IReadOnlyList<SessionTranscriptInfo>> ListSessionTranscriptsAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        _sessionArtifacts.ListSessionTranscriptsAsync(sessionId, cancellationToken);

    public Task<string?> ReadSessionTranscriptAsync(
        string sessionId,
        string fileName,
        int offsetChars,
        int limitChars,
        CancellationToken cancellationToken = default) =>
        _sessionArtifacts.ReadSessionTranscriptAsync(sessionId, fileName, offsetChars, limitChars, cancellationToken);

    public Task<IReadOnlyList<SessionTranscriptMatch>> SearchSessionTranscriptsAsync(
        string sessionId,
        string query,
        int limit,
        int previewChars,
        CancellationToken cancellationToken = default) =>
        _sessionArtifacts.SearchSessionTranscriptsAsync(sessionId, query, limit, previewChars, cancellationToken);

    public Task<string> ReadHandoffNoteAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _sessionArtifacts.ReadHandoffNoteAsync(sessionId, cancellationToken);

    public Task<HandoffNoteAppendResult> TryAppendHandoffNoteAsync(
        string sessionId,
        string text,
        CancellationToken cancellationToken = default) =>
        _sessionArtifacts.TryAppendHandoffNoteAsync(sessionId, text, cancellationToken);

    public Task AppendAttemptEventAsync(
        string sessionId,
        AgentAttemptEvent entry,
        CancellationToken cancellationToken = default) =>
        _attemptEvents.AppendAttemptEventAsync(sessionId, entry, cancellationToken);

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        _appSettings.SaveSettingsAsync(settings, cancellationToken);

    public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
        _appSettings.LoadSettingsAsync(cancellationToken);
}
