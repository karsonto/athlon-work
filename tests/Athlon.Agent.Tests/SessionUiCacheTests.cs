using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class SessionUiCacheTests
{
    [Fact]
    public async Task Touch_evicts_oldest_session_and_clears_messages()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var cache = new SessionUiCache(dispatcher);

        await dispatcher.InvokeAsync(() =>
        {
            var first = cache.GetOrCreate("session-1");
            first.Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "hello")));
            cache.GetOrCreate("session-2");
            cache.GetOrCreate("session-3");
            cache.GetOrCreate("session-4");
            cache.GetOrCreate("session-5");
            cache.GetOrCreate("session-6");
            cache.GetOrCreate("session-7");
            cache.GetOrCreate("session-8");
            cache.GetOrCreate("session-9");

            Assert.False(cache.TryGet("session-1", out _));
            Assert.Empty(first.Messages);
        });
    }

    [Fact]
    public async Task Remove_clears_controller_messages()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var cache = new SessionUiCache(dispatcher);

        await dispatcher.InvokeAsync(() =>
        {
            var controller = cache.GetOrCreate("session-a");
            controller.Messages.Add(new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, "x")));
            cache.Remove("session-a");

            Assert.False(cache.TryGet("session-a", out _));
        });
    }

    private static Task<Dispatcher> StartStaDispatcherAsync()
    {
        var tcs = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            tcs.SetResult(dispatcher);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
