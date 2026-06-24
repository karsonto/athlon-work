using Athlon.Agent.App.Services;
using Athlon.Agent.Core.Events;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

[Collection(TestCollections.Sta)]
public sealed class AgentRunEventBridgeTests
{
    [Fact]
    public async Task BuildCallbacks_ForwardsStreamEventsThroughSink()
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var ui = new SessionTurnUiController(dispatcher);
        var bridge = new AgentRunEventBridge();
        var callbacks = bridge.BuildCallbacks(ui);

        AgentStreamEvent? received = null;
        bridge.Multiplexer.SubscribeStream((streamEvent, _) =>
        {
            received = streamEvent;
            return ValueTask.CompletedTask;
        });

        Assert.NotNull(callbacks.EventSink);
        await callbacks.EventSink.PublishStreamEventAsync(new AgentStreamEvent.RunStarted("session", "run"));

        Assert.IsType<AgentStreamEvent.RunStarted>(received);
    }
}
