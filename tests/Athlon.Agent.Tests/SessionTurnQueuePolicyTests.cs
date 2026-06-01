using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SessionTurnQueuePolicyTests
{
    [Fact]
    public void HasQueuedTurns_BlocksPlanContinueUntilQueueEmpty()
    {
        var host = new SessionTurnHost(new NoOpOrchestrator(), new NoOpStorage(), new AppSettings());
        var sessionId = "plan-session";
        host.Enqueue(new QueuedTurnPayload("q1", sessionId, "next", Array.Empty<ImageAttachment>(), null!));

        Assert.True(host.HasQueuedTurns(sessionId));
        Assert.False(ShouldSchedulePlanAutoContinue(host, sessionId));
    }

    [Fact]
    public void HasQueuedTurns_AllowsPlanContinueWhenQueueEmpty()
    {
        var host = new SessionTurnHost(new NoOpOrchestrator(), new NoOpStorage(), new AppSettings());
        var sessionId = "plan-empty";

        Assert.False(host.HasQueuedTurns(sessionId));
        Assert.True(ShouldSchedulePlanAutoContinue(host, sessionId));
    }

    private static bool ShouldSchedulePlanAutoContinue(SessionTurnHost host, string sessionId) =>
        !host.HasQueuedTurns(sessionId);

    private sealed class NoOpOrchestrator : IAgentOrchestrator
    {
        public Task<AgentSession> SendAsync(
            AgentSession session,
            string userInput,
            IReadOnlyList<ImageAttachment>? imageAttachments = null,
            AgentTurnCallbacks? callbacks = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(session);
    }

    private sealed class NoOpStorage : IFileStorageService
    {
        public string RootPath { get; } = Path.GetTempPath();
        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<AgentSession?>(null);
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }
}
