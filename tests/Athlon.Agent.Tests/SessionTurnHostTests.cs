using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

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

    private static SessionTurnHost CreateHost(IAgentOrchestrator orchestrator) =>
        new(orchestrator, new NoOpStorage(), new AppSettings());

    private static bool TryStart(SessionTurnHost host, Dispatcher dispatcher, AgentSession session, out string? error)
    {
        var ui = new SessionTurnUiController(dispatcher);
        return host.TryStart(new SessionTurnRequest(session.Id, session, "hi", Array.Empty<ImageAttachment>(), ui), out error);
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
        public Task AppendToolCallLogAsync(string sessionId, SessionToolCallLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
