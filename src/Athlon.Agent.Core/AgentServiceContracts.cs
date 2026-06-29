using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public interface IActiveWorkspaceContext
{
    string? RootPath { get; }
    string? DisplayName { get; }
    IReadOnlyList<string> IgnorePatterns { get; }
    void SetWorkspace(string? rootPath, string? displayName = null, IReadOnlyList<string>? ignorePatterns = null);
}
public interface IAgentEnvironmentPromptBuilder
{
    string Build(AgentSession session, IReadOnlyList<ToolDefinition> tools);
}

/// <summary>Current Windows desktop host facts injected into the agent environment prompt.</summary>
public interface IAgentHostEnvironment
{
    bool IsWindows { get; }
    string OsDescription { get; }
    string OsVersion { get; }
    string UserName { get; }
    string UserDomainName { get; }
    string MachineName { get; }
    string UserProfilePath { get; }
    string SystemDirectory { get; }
    string ProcessArchitecture { get; }
    string OsArchitecture { get; }
    int ProcessorCount { get; }
    string AppDataDirectory { get; }
    string SkillsDirectory { get; }
}
public sealed record AvailableSkillInfo(string Name, string Description, string SkillId);
public interface IAvailableSkillsProvider
{
    IReadOnlyList<AvailableSkillInfo> GetSkills();
}
public interface IAgentRuntime
{
    Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments = null,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default);
}
public interface IFileStorageService
{
    string RootPath { get; }
    Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default);
    Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default);
    Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);
    Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default);
    Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default);
    Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default);
    Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default);
    Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
}
public interface IAppLogger
{
    void Debug(string messageTemplate, params object[] values);
    void Information(string messageTemplate, params object[] values);
    void Warning(string messageTemplate, params object[] values);
    void Error(Exception exception, string messageTemplate, params object[] values);
    IAppLogger ForContext(string sourceContext);
}

/// <summary>Append-only trace log written to startup.log during application bootstrap.</summary>
public interface IStartupLog
{
    void Write(string message);
}
public interface ICredentialStore
{
    Task SaveSecretAsync(string name, string secret, CancellationToken cancellationToken = default);
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default);
    Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken = default);
}
public interface IAgentOrchestrator
{
    Task<AgentSession> SendAsync(
        AgentSession session,
        string userInput,
        IReadOnlyList<ImageAttachment>? imageAttachments = null,
        AgentTurnCallbacks? callbacks = null,
        CancellationToken cancellationToken = default);
}

public interface IImageAttachmentReader
{
    Task<IReadOnlyList<ImageAttachment>> ReadImagesAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);
}
