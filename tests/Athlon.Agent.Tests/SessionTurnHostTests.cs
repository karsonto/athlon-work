using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.Slow)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class SessionTurnHostTests
{
    [Fact]
    public async Task TryStart_RejectsFourthConcurrentTurn()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var host = CreateHost(new SlowOrchestrator(TimeSpan.FromSeconds(2)));
        var sessions = Enumerable.Range(0, 4).Select(_ => AgentSession.Create(Guid.NewGuid().ToString("N"))).ToArray();

        Assert.True(TryStart(host, dispatcher, sessions[0], out _));
        Assert.True(TryStart(host, dispatcher, sessions[1], out _));
        Assert.True(TryStart(host, dispatcher, sessions[2], out _));
        Assert.False(TryStart(host, dispatcher, sessions[3], out var error));
        Assert.Contains("3", error, StringComparison.Ordinal);

        host.CancelAll();
        await Task.Delay(100);
    }

    [Fact]
    public async Task TryStart_RejectsSecondTurnOnSameSession()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var host = CreateHost(new SlowOrchestrator(TimeSpan.FromSeconds(2)));
        var session = AgentSession.Create("same");

        Assert.True(TryStart(host, dispatcher, session, out _));
        Assert.False(TryStart(host, dispatcher, session, out var error));
        Assert.Contains("正在生成", error, StringComparison.Ordinal);

        host.Cancel(session.Id);
    }

    [Fact]
    public async Task Enqueue_WhenSessionRunning_AddsToQueue()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var host = CreateHost(new SlowOrchestrator(TimeSpan.FromSeconds(2)));
        var session = AgentSession.Create("queued");

        Assert.True(TryStart(host, dispatcher, session, out _));
        host.Enqueue(CreatePayload(dispatcher, session.Id, "q1"));
        Assert.Equal(1, host.GetQueueCount(session.Id));

        host.Cancel(session.Id);
        await Task.Delay(200);
    }

    [Fact]
    public async Task TryStart_WhenSessionRunning_StillRejectedAfterEnqueue()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var host = CreateHost(new SlowOrchestrator(TimeSpan.FromSeconds(2)));
        var session = AgentSession.Create("busy");

        Assert.True(TryStart(host, dispatcher, session, out _));
        host.Enqueue(CreatePayload(dispatcher, session.Id, "queued"));
        Assert.False(TryStart(host, dispatcher, session, out var error));
        Assert.Contains("正在生成", error, StringComparison.Ordinal);

        host.Cancel(session.Id);
        await Task.Delay(200);
    }

    [Fact]
    public void ClearQueue_RemovesAllPending()
    {
        var host = CreateHost(new SlowOrchestrator(TimeSpan.FromMilliseconds(1)));
        var sessionId = "clear-me";
        host.Enqueue(new QueuedTurnPayload("a", sessionId, "one", Array.Empty<ImageAttachment>(), null!));
        host.Enqueue(new QueuedTurnPayload("b", sessionId, "two", Array.Empty<ImageAttachment>(), null!));
        Assert.Equal(2, host.GetQueueCount(sessionId));

        host.ClearQueue(sessionId);
        Assert.Equal(0, host.GetQueueCount(sessionId));
    }

    [Fact]
    public void TryDequeue_ReturnsFifoOrder()
    {
        var host = CreateHost(new SlowOrchestrator(TimeSpan.FromMilliseconds(1)));
        var sessionId = "fifo";
        host.Enqueue(new QueuedTurnPayload("first", sessionId, "1", Array.Empty<ImageAttachment>(), null!));
        host.Enqueue(new QueuedTurnPayload("second", sessionId, "2", Array.Empty<ImageAttachment>(), null!));

        Assert.True(host.TryDequeue(sessionId, out var one));
        Assert.Equal("first", one!.QueueId);
        Assert.True(host.TryDequeue(sessionId, out var two));
        Assert.Equal("second", two!.QueueId);
        Assert.False(host.TryDequeue(sessionId, out _));
    }

    [Fact]
    public void Remove_RemovesMatchingQueueId()
    {
        var host = CreateHost(new SlowOrchestrator(TimeSpan.FromMilliseconds(1)));
        var sessionId = "remove";
        host.Enqueue(new QueuedTurnPayload("keep", sessionId, "k", Array.Empty<ImageAttachment>(), null!));
        host.Enqueue(new QueuedTurnPayload("drop", sessionId, "d", Array.Empty<ImageAttachment>(), null!));

        Assert.True(host.Remove(sessionId, "drop"));
        Assert.Equal(1, host.GetQueueCount(sessionId));
        Assert.True(host.TryDequeue(sessionId, out var remaining));
        Assert.Equal("keep", remaining!.QueueId);
    }

    [Fact]
    public async Task Cancel_OnlyStopsTargetSession()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var host = CreateHost(new SlowOrchestrator(TimeSpan.FromSeconds(5)));
        var sessionA = AgentSession.Create("a");
        var sessionB = AgentSession.Create("b");

        Assert.True(TryStart(host, dispatcher, sessionA, out _));
        Assert.True(TryStart(host, dispatcher, sessionB, out _));
        Assert.True(host.IsRunning(sessionA.Id));
        Assert.True(host.IsRunning(sessionB.Id));

        host.Cancel(sessionA.Id);
        await Task.Delay(200);

        Assert.False(host.IsRunning(sessionA.Id));
        Assert.True(host.IsRunning(sessionB.Id));

        host.CancelAll();
        await Task.Delay(200);
    }

    [Fact]
    public async Task ShutdownAsync_WhenRunnerCancelled_ClearsRunners()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var host = CreateHost(new SlowOrchestrator(TimeSpan.FromSeconds(30)));
        var session = AgentSession.Create("shutdown");

        Assert.True(TryStart(host, dispatcher, session, out _));
        Assert.True(host.HasActiveWork);

        await host.ShutdownAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);

        Assert.False(host.IsRunning(session.Id));
    }

    [Fact]
    public async Task HasActiveWork_WhenQueueHasItems_ReturnsTrueUntilCleared()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var host = CreateHost(new SlowOrchestrator(TimeSpan.FromMilliseconds(1)));

        host.Enqueue(CreatePayload(dispatcher, "queued-session", "hello"));
        Assert.True(host.HasActiveWork);

        host.ClearAllQueues();
        Assert.False(host.HasActiveWork);
    }

    private static SessionTurnHost CreateHost(IAgentOrchestrator orchestrator) =>
        new(orchestrator, new NoOpStorage(), new AppSettings());

    private static bool TryStart(SessionTurnHost host, Dispatcher dispatcher, AgentSession session, out string? error)
    {
        var ui = new SessionTurnUiController(dispatcher);
        return host.TryStart(new SessionTurnRequest(session.Id, session, "hi", Array.Empty<ImageAttachment>(), ui), out error);
    }

    private static QueuedTurnPayload CreatePayload(Dispatcher dispatcher, string sessionId, string input)
    {
        var ui = new SessionTurnUiController(dispatcher);
        return new QueuedTurnPayload(Guid.NewGuid().ToString("N"), sessionId, input, Array.Empty<ImageAttachment>(), ui);
    }

    private static Task<Dispatcher> StartStaDispatcherAsync()
    {
        var tcs = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            tcs.SetResult(dispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private sealed class SlowOrchestrator(TimeSpan delay) : IAgentOrchestrator
    {
        public async Task<AgentSession> SendAsync(
            AgentSession session,
            string userInput,
            IReadOnlyList<ImageAttachment>? imageAttachments = null,
            AgentTurnCallbacks? callbacks = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return session.WithMessages(session.Messages.Append(ChatMessage.Create(MessageRole.Assistant, "ok")).ToArray());
        }
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

        public Task SaveContextSummaryAsync(ContextSummary summary, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> SaveTranscriptAsync(
            string sessionId,
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<string> SaveEvictedToolResultAsync(
            string sessionId,
            string toolCallId,
            string content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }
}
