using System.Windows.Threading;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

[Collection(TestCollections.Sta)]
[Trait("Category", TestCategories.UsesSta)]
public sealed class SessionTurnUiControllerApprovalTests
{
    [Fact]
    public async Task RequestToolApproval_ShowToolCallsDisabled_AddsPendingToolBubble()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetShowToolCalls(false);
        ui.SetDisplayed(true);

        var approval = new PendingToolApproval(
            "call-approval",
            "file_write",
            ToolCallArgumentsParser.ParseJson("""{"path":"approval-test.txt","content":"hello"}"""),
            ToolInvocationPolicy.Ask,
            DateTimeOffset.UtcNow);

        var callbacks = ui.BuildCallbacks();
        var decisionTask = callbacks.OnToolApprovalRequested!(approval, CancellationToken.None);

        await dispatcher.InvokeAsync(() =>
        {
            var bubble = Assert.Single(ui.Messages, message => message.IsTool);
            Assert.Equal("call-approval", bubble.ToolCallId);
            Assert.Equal(ToolApprovalState.Pending, bubble.ToolApprovalState);
            Assert.Equal(ToolCallDisplayStatus.AwaitingApproval, bubble.ToolCallStatus);
            Assert.Contains("approval-test.txt", bubble.ToolApprovalArgumentsPreview, StringComparison.Ordinal);
            Assert.True(ui.TryResolveToolApproval("call-approval", ToolApprovalDecision.Approved));
        });

        var decision = await decisionTask;
        Assert.Equal(ToolApprovalDecision.Approved, decision);

        await dispatcher.InvokeAsync(() =>
        {
            var bubble = Assert.Single(ui.Messages, message => message.IsTool);
            Assert.Equal(ToolApprovalState.Approved, bubble.ToolApprovalState);
            Assert.Equal(ToolCallDisplayStatus.Running, bubble.ToolCallStatus);
            Assert.Equal(0, ui.PendingApprovalCount);
        });
    }

    [Fact]
    public async Task RequestToolApproval_ParallelPendingApprovals_ResolveIndependently()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetDisplayed(true);

        var approvalA = new PendingToolApproval(
            "call-a",
            "file_write",
            ToolCallArgumentsParser.ParseJson("""{"path":"a.txt","content":"a"}"""),
            ToolInvocationPolicy.Ask,
            DateTimeOffset.UtcNow);
        var approvalB = new PendingToolApproval(
            "call-b",
            "execute_command",
            ToolCallArgumentsParser.ParseJson("""{"command":"echo b"}"""),
            ToolInvocationPolicy.Ask,
            DateTimeOffset.UtcNow);

        var callbacks = ui.BuildCallbacks();
        var decisionATask = callbacks.OnToolApprovalRequested!(approvalA, CancellationToken.None);
        var decisionBTask = callbacks.OnToolApprovalRequested!(approvalB, CancellationToken.None);

        await dispatcher.InvokeAsync(() =>
        {
            Assert.Equal(2, ui.PendingApprovalCount);
            Assert.True(ui.TryResolveToolApproval("call-a", ToolApprovalDecision.Denied));
            Assert.True(ui.TryResolveToolApproval("call-b", ToolApprovalDecision.Approved));
        });

        Assert.Equal(ToolApprovalDecision.Denied, await decisionATask);
        Assert.Equal(ToolApprovalDecision.Approved, await decisionBTask);
    }

    [Fact]
    public async Task RestorePendingToolApprovals_AfterDisplayedToggle_KeepsPendingState()
    {
        var dispatcher = await StartStaDispatcherAsync();
        var ui = new SessionTurnUiController(dispatcher);
        ui.SetDisplayed(false);

        var approval = new PendingToolApproval(
            "call-restore",
            "file_write",
            ToolCallArgumentsParser.ParseJson("""{"path":"restore.txt","content":"x"}"""),
            ToolInvocationPolicy.Ask,
            DateTimeOffset.UtcNow);

        var callbacks = ui.BuildCallbacks();
        var decisionTask = callbacks.OnToolApprovalRequested!(approval, CancellationToken.None);

        await dispatcher.InvokeAsync(() =>
        {
            var bubble = Assert.Single(ui.Messages, message => message.IsTool);
            Assert.Equal(ToolApprovalState.Pending, bubble.ToolApprovalState);
            Assert.Equal(1, ui.PendingApprovalCount);
        });

        ui.SetDisplayed(true);
        await ui.RestorePendingToolApprovalsAsync();

        await dispatcher.InvokeAsync(() =>
        {
            Assert.Equal(1, ui.PendingApprovalCount);
            Assert.True(ui.TryResolveToolApproval("call-restore", ToolApprovalDecision.Denied));
        });

        Assert.Equal(ToolApprovalDecision.Denied, await decisionTask);
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
