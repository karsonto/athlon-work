using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Tests;

internal sealed class TempDirectoryScope : IDisposable
{
    public TempDirectoryScope(string prefix)
    {
        Root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string GetPath(params string[] paths) => Path.Combine([Root, .. paths]);

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class NoOpStorage : IFileStorageService
{
    public string RootPath => "/tmp";
    public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<AgentSession?>(null);
    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.FromResult("/tmp/t.jsonl");
    public Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default) => Task.FromResult("/tmp/evicted.txt");
    public Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
    public Task<ConversationDisplayPage> LoadConversationDisplayPageAsync(string sessionId, ConversationDisplayCursor? cursor = null, int pageSize = ConversationDisplayLimits.PageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConversationDisplayPage(Array.Empty<ChatMessage>(), null));
    public Task ReplaceConversationDisplayAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
}

internal sealed class NoOpLogger : IAppLogger
{
    public void Debug(string messageTemplate, params object[] values) { }
    public void Information(string messageTemplate, params object[] values) { }
    public void Warning(string messageTemplate, params object[] values) { }
    public void Error(Exception exception, string messageTemplate, params object[] values) { }
    public IAppLogger ForContext(string sourceContext) => this;
    public void Dispose() { }
}

internal sealed class NoOpToolRouter : IToolRouter
{
    public IReadOnlyList<ToolDefinition> ListTools() => Array.Empty<ToolDefinition>();
    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolResult.Success("ok"));
}

internal sealed class NoOpActiveAgentSessionContext : IActiveAgentSessionContext
{
    private static readonly AsyncLocal<string?> AmbientSessionId = new();

    public string? SessionId => AmbientSessionId.Value;

    public void SetSession(string? sessionId) => AmbientSessionId.Value = sessionId;

    public IDisposable Enter(string sessionId)
    {
        var previous = AmbientSessionId.Value;
        AmbientSessionId.Value = sessionId;
        return new SessionScope(previous);
    }

    private sealed class SessionScope(string? previous) : IDisposable
    {
        public void Dispose() => AmbientSessionId.Value = previous;
    }
}

internal sealed class NoOpPreCompletionPipeline : IPreCompletionPipeline
{
    public Task<AgentSession> RunAsync(
        AgentSession session,
        PreCompletionOptions? options = null,
        CompactionRuntimeContext? runtimeContext = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(session);
}

internal sealed class NoOpToolResultEvictor : IToolResultEvictor
{
    public Task<string> EvictIfNeededAsync(
        string sessionId,
        AgentToolCall toolCall,
        ToolResult result,
        string formattedToolContent,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(formattedToolContent);
}

internal sealed class NoOpTokenEstimator : ITokenEstimatorCalibrator
{
    public double GetMultiplier(string sessionId) => 1;

    public void Observe(string sessionId, int estimatedPromptTokens, int? actualPromptTokens) { }
}
