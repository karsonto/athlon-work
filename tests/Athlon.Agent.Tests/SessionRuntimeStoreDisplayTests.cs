using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class SessionRuntimeStoreDisplayTests
{
    [Fact]
    public async Task TryGetHydrated_is_false_when_ui_is_empty_but_session_has_messages()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var cache = new SessionUiCache(dispatcher, new AppSettings());
        using var store = new SessionRuntimeStore(new NoOpStorage(), cache, enablePeriodicFlush: false);
        var session = AgentSession.Create("hist")
            .WithMessage(ChatMessage.Create(MessageRole.User, "hello"));

        await dispatcher.InvokeAsync(() => cache.GetOrCreate(session.Id));
        store.Attach(session, hydrated: true);

        Assert.False(store.TryGetHydrated(session.Id, out _));
    }

    [Fact]
    public async Task TryGetHydrated_is_true_when_ui_has_messages()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var cache = new SessionUiCache(dispatcher, new AppSettings());
        using var store = new SessionRuntimeStore(new NoOpStorage(), cache, enablePeriodicFlush: false);
        var session = AgentSession.Create("hist")
            .WithMessage(ChatMessage.Create(MessageRole.User, "hello"));

        await dispatcher.InvokeAsync(() =>
        {
            var ui = cache.GetOrCreate(session.Id);
            ui.Messages.Add(new ChatMessageViewModel(session.Messages[0]));
        });
        store.Attach(session);

        Assert.True(store.TryGetHydrated(session.Id, out var live));
        Assert.Equal(session.Id, live.Session!.Id);
    }

    [Fact]
    public async Task TryGetHydrated_is_true_for_empty_chat_with_empty_ui()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var cache = new SessionUiCache(dispatcher, new AppSettings());
        using var store = new SessionRuntimeStore(new NoOpStorage(), cache, enablePeriodicFlush: false);
        var session = AgentSession.Create("New Chat");

        await dispatcher.InvokeAsync(() => cache.GetOrCreate(session.Id));
        store.Attach(session, hydrated: true);

        Assert.True(store.TryGetHydrated(session.Id, out var live));
        Assert.Equal(session.Id, live.Session!.Id);
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
