using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class SessionRuntimeStoreDisplayTests
{
    [Fact]
    public async Task TryGetHydrated_is_true_when_attached_and_marked_hydrated()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var cache = new SessionUiCache(dispatcher, new AppSettings());
        using var store = new SessionRuntimeStore(new NoOpStorage(), cache);
        var session = AgentSession.Create("hist")
            .WithMessage(ChatMessage.Create(MessageRole.User, "hello"));

        await dispatcher.InvokeAsync(() => cache.GetOrCreate(session.Id));
        store.Attach(session, hydrated: true);

        Assert.True(store.TryGetHydrated(session.Id, out var live));
        Assert.Equal(session.Id, live.Session!.Id);
    }

    [Fact]
    public async Task TryGetHydrated_is_false_before_mark_hydrated()
    {
        using var store = new SessionRuntimeStore(new NoOpStorage());
        var session = AgentSession.Create("hist")
            .WithMessage(ChatMessage.Create(MessageRole.User, "hello"));

        store.Attach(session);
        Assert.False(store.TryGetHydrated(session.Id, out _));

        store.MarkHydrated(session.Id, olderDisplayCursor: null);
        Assert.True(store.TryGetHydrated(session.Id, out var live));
        Assert.Equal(session.Id, live.Session!.Id);
    }

    [Fact]
    public async Task TryGetHydrated_is_true_for_empty_chat()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var cache = new SessionUiCache(dispatcher, new AppSettings());
        using var store = new SessionRuntimeStore(new NoOpStorage(), cache);
        var session = AgentSession.Create("New Chat");

        await dispatcher.InvokeAsync(() => cache.GetOrCreate(session.Id));
        store.Attach(session, hydrated: true);

        Assert.True(store.TryGetHydrated(session.Id, out var live));
        Assert.Equal(session.Id, live.Session!.Id);
    }

    private sealed class NoOpStorage : IFileStorageService
    {
        public string RootPath => "/tmp";

        public Task SaveSessionAsync(AgentSession session, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AgentSession?> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentSession?>(null);

        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

        public Task AppendConversationMessageAsync(
            string sessionId,
            ChatMessage message,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ChatMessage>> LoadConversationDisplayAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());

        public Task ReplaceConversationDisplayAsync(
            string sessionId,
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearConversationDisplayAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AppendToolCallLogAsync(
            string sessionId,
            SessionToolCallLogEntry entry,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task FlushPendingToolCallLogsAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SessionIndexEntry>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionIndexEntry>>(Array.Empty<SessionIndexEntry>());

        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());
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
}
