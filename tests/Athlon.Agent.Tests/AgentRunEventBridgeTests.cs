using Athlon.Agent.App.Services;
using Athlon.Agent.Core;
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

    [Theory]
    [InlineData(true, ToolApprovalDecision.Approved)]
    [InlineData(false, ToolApprovalDecision.Denied)]
    public async Task BuildCallbacks_ForwardsUiToolApprovalDecision(
        bool approved,
        ToolApprovalDecision expected)
    {
        var ui = new SessionTurnUiController(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var callbacks = new AgentRunEventBridge().BuildCallbacks(ui);
        var approval = new PendingToolApproval(
            "call-1",
            "file_write",
            ToolCallArgumentsParser.ParseJson("""{"path":"src/App.cs","content":"hello"}"""),
            ToolInvocationPolicy.Ask,
            DateTimeOffset.UtcNow);

        Assert.NotNull(callbacks.OnToolApprovalRequested);
        var decisionTask = callbacks.OnToolApprovalRequested!(approval, CancellationToken.None);
        Assert.True(ui.TryResolveToolApproval(
            approval.ToolCallId,
            approved ? ToolApprovalDecision.Approved : ToolApprovalDecision.Denied));
        var decision = await decisionTask;

        Assert.Equal(expected, decision);
    }

    [Fact]
    public void BuildCallbacks_WithoutUiNotifier_StillProvidesBubbleApprovalCallback()
    {
        var ui = new SessionTurnUiController(System.Windows.Threading.Dispatcher.CurrentDispatcher);

        Assert.NotNull(new AgentRunEventBridge().BuildCallbacks(ui).OnToolApprovalRequested);
    }
}
