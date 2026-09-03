using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SessionTurnQueuePolicyTests
{
    [Fact]
    public void HasQueuedTurns_ReturnsTrueWhenQueueNotEmpty()
    {
        var host = new SessionTurnHost(
            new NoOpOrchestrator(),
            RouterTestDependencies.CreateDebugTurnOrchestrator(),
            RouterTestDependencies.CreatePlanTurnOrchestrator(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new NoOpStorage(),
            new AppSettings());
        var sessionId = "queued-session";
        host.Enqueue(new QueuedTurnPayload("q1", sessionId, "next", Array.Empty<ImageAttachment>(), null!));

        Assert.True(host.HasQueuedTurns(sessionId));
    }

    [Fact]
    public void HasQueuedTurns_ReturnsFalseWhenQueueEmpty()
    {
        var host = new SessionTurnHost(
            new NoOpOrchestrator(),
            RouterTestDependencies.CreateDebugTurnOrchestrator(),
            RouterTestDependencies.CreatePlanTurnOrchestrator(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new NoOpStorage(),
            new AppSettings());
        var sessionId = "empty-session";

        Assert.False(host.HasQueuedTurns(sessionId));
    }

    [Fact]
    public void Presenter_Update_RejectsEmptyWithoutImages_AllowsWithImages()
    {
        var host = new SessionTurnHost(
            new NoOpOrchestrator(),
            RouterTestDependencies.CreateDebugTurnOrchestrator(),
            RouterTestDependencies.CreatePlanTurnOrchestrator(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new NoOpStorage(),
            new AppSettings());
        var presenter = new QueuedTurnPresenter(host);
        var sessionId = "edit-queue";
        var images = new[]
        {
            new ImageAttachment("a.png", "image/png", "data:image/png;base64,AA=="),
        };
        var replacementImages = new[]
        {
            new ImageAttachment("b.png", "image/png", "data:image/png;base64,BB=="),
        };

        presenter.Enqueue(sessionId, "text-only", "hello", Array.Empty<ImageAttachment>(), null!);
        Assert.False(presenter.Update(sessionId, "text-only", "   ", Array.Empty<ImageAttachment>()));
        Assert.True(presenter.Update(sessionId, "text-only", "updated", Array.Empty<ImageAttachment>()));

        presenter.Enqueue(sessionId, "with-image", "caption", images, null!);
        Assert.True(presenter.Update(sessionId, "with-image", "  ", images));
        Assert.True(presenter.Update(sessionId, "with-image", string.Empty, replacementImages));

        Assert.True(host.TryDequeue(sessionId, out var first));
        Assert.Equal("updated", first!.UserInput);
        Assert.True(host.TryDequeue(sessionId, out var second));
        Assert.Equal(string.Empty, second!.UserInput);
        Assert.Single(second.ImageAttachments);
        Assert.Equal("b.png", second.ImageAttachments[0].FileName);
    }

    private sealed class NoOpOrchestrator : IAgentOrchestrator
    {
        public Task<AgentSession> SendAsync(
            AgentSession session,
            string userInput,
            IReadOnlyList<ImageAttachment>? imageAttachments = null,
            AgentTurnCallbacks? callbacks = null,
            CancellationToken cancellationToken = default,
            bool computerUseActive = false,
            bool appendUserMessage = true) =>
            Task.FromResult(session);
    }

    private sealed class NoOpStorage : IFileStorageService
    {
        public string RootPath { get; } = Path.GetTempPath();
        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<AgentSession?>(null);
        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendConversationMessageAsync(string sessionId, ChatMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
        public Task ReplaceConversationDisplayAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AppSettings());
        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SaveTranscriptAsync(string sessionId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> SaveEvictedToolResultAsync(string sessionId, string toolCallId, string content, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }
}
