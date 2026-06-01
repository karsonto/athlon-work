using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SessionTurnQueueImageTests
{
    [Fact]
    public void Enqueue_Dequeue_PreservesImageAttachments()
    {
        var host = new SessionTurnHost(new NoOpOrchestrator(), new NoOpStorage(), new AppSettings());
        var sessionId = "session-images";
        var images = new[]
        {
            new ImageAttachment("shot.png", "image/png", "data:image/png;base64,AA=="),
            new ImageAttachment("chart.jpg", "image/jpeg", "data:image/jpeg;base64,BB=="),
        };

        host.Enqueue(new QueuedTurnPayload("q1", sessionId, "请分析", images, null!));

        Assert.True(host.TryDequeue(sessionId, out var payload));
        Assert.NotNull(payload);
        Assert.Equal(2, payload.ImageAttachments.Count);
        Assert.Equal("shot.png", payload.ImageAttachments[0].FileName);
        Assert.Equal("请分析", payload.UserInput);
    }

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
