using Athlon.Agent.App.Services;
using Athlon.Agent.App.Localization;
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
        bool confirm,
        ToolApprovalDecision expected)
    {
        var notifier = new CapturingNotifier(confirm);
        var ui = new SessionTurnUiController(
            System.Windows.Threading.Dispatcher.CurrentDispatcher,
            notifier: notifier);
        var callbacks = new AgentRunEventBridge().BuildCallbacks(ui);
        var approval = new PendingToolApproval(
            "call-1",
            "file_write",
            ToolCallArgumentsParser.ParseJson("""{"path":"src/App.cs","content":"hello"}"""),
            ToolInvocationPolicy.Ask,
            DateTimeOffset.UtcNow);

        Assert.NotNull(callbacks.OnToolApprovalRequested);
        var decision = await callbacks.OnToolApprovalRequested!(approval, CancellationToken.None);

        Assert.Equal(expected, decision);
        Assert.Contains("file_write", notifier.LastArgs[0]?.ToString(), StringComparison.Ordinal);
        Assert.Contains("src/App.cs", notifier.LastArgs[1]?.ToString(), StringComparison.Ordinal);
        Assert.Contains("5 chars", notifier.LastArgs[1]?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCallbacks_WithoutUiNotifier_LeavesApprovalCallbackAbsent()
    {
        var ui = new SessionTurnUiController(System.Windows.Threading.Dispatcher.CurrentDispatcher);

        Assert.Null(new AgentRunEventBridge().BuildCallbacks(ui).OnToolApprovalRequested);
    }

    private sealed class CapturingNotifier(bool confirmation) : IUserNotifier
    {
        public object[] LastArgs { get; private set; } = [];
        public void Info(string titleKey, string messageKey, params object[] messageArgs) { }
        public void Warning(string titleKey, string messageKey, params object[] messageArgs) { }
        public void InfoText(string titleKey, string messageText) { }
        public void WarningText(string titleKey, string messageText) { }
        public bool Confirm(string titleKey, string messageKey, params object[] messageArgs) => confirmation;
        public bool ConfirmYesNo(string titleKey, string messageKey, params object[] messageArgs)
        {
            LastArgs = messageArgs;
            return confirmation;
        }
        public System.Windows.MessageBoxResult AskYesNoCancel(
            string titleKey,
            string messageKey,
            params object[] messageArgs) =>
            System.Windows.MessageBoxResult.Cancel;
    }
}
