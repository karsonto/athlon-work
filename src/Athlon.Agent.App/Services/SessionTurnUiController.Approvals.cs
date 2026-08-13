using Athlon.Agent.App.Controls;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.App.Services;

public sealed partial class SessionTurnUiController
{
    private async Task<ToolApprovalDecision> RequestToolApprovalAsync(
        PendingToolApproval approval,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = ToolMessageDisplayParser.FormatArgumentsFull(approval.Arguments, approval.ToolName);
        if (arguments.Length > 1200)
        {
            arguments = arguments[..1200] + "…";
        }

        var completion = new TaskCompletionSource<ToolApprovalDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingUiApproval(approval, arguments, completion);
        if (!_pendingApprovals.TryAdd(approval.ToolCallId, pending))
        {
            throw new InvalidOperationException(
                $"Tool approval '{approval.ToolCallId}' is already pending.");
        }

        try
        {
            await RunOnUiAsync(() => EnsureToolApprovalBubble(pending)).ConfigureAwait(false);

            if (IsDisplayed)
            {
                await ShowToolApprovalAsync(pending).ConfigureAwait(false);
            }

            var decision = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await RunOnUiAsync(() => ApplyToolApprovalDecisionToViewModel(approval.ToolCallId, decision))
                .ConfigureAwait(false);

            if (IsDisplayed)
            {
                await ResolveToolApprovalAsync(approval.ToolCallId, decision).ConfigureAwait(false);
            }

            return decision;
        }
        finally
        {
            _pendingApprovals.TryRemove(approval.ToolCallId, out _);
        }
    }

    internal int PendingApprovalCount => _pendingApprovals.Count;

    private void EnsureToolApprovalBubble(PendingUiApproval pending)
    {
        var existing = FindToolMessage(pending.Approval.ToolCallId);
        if (existing is not null)
        {
            existing.MarkAwaitingApproval(pending.Arguments);
            DispatchApprovalToolCardIfNeeded(existing, pending, createdNew: false);
            RequestScrollImmediate();
            return;
        }

        var toolCall = new AgentToolCall(
            pending.Approval.ToolCallId,
            pending.Approval.ToolName,
            pending.Approval.Arguments);
        var bubble = ChatMessageViewModel.CreatePendingTool(toolCall);
        bubble.MarkAwaitingApproval(pending.Arguments);
        Messages.Add(bubble);
        TrimMessagesIfNeeded();
        DispatchApprovalToolCardIfNeeded(bubble, pending, createdNew: true);
        RequestScrollImmediate();
    }

    private void ApplyToolApprovalDecisionToViewModel(string toolCallId, ToolApprovalDecision decision)
    {
        FindToolMessage(toolCallId)?.ApplyToolApprovalDecision(decision);
    }

    private void DispatchApprovalToolCardIfNeeded(
        ChatMessageViewModel bubble,
        PendingUiApproval pending,
        bool createdNew)
    {
        if (!createdNew || !IsDisplayed || ChatView is null)
        {
            return;
        }

        var toolCallId = bubble.ToolCallId ?? pending.Approval.ToolCallId;
        _ = ChatView.DispatchEventAsync(new AgentStreamEvent.ToolCallStart(toolCallId, pending.Approval.ToolName, null));
        if (!string.IsNullOrWhiteSpace(pending.Arguments))
        {
            _ = ChatView.DispatchEventAsync(new AgentStreamEvent.ToolCallArgs(toolCallId, pending.Arguments));
        }

        _ = ChatView.DispatchEventAsync(new AgentStreamEvent.ToolCallEnd(toolCallId));
    }

    internal bool TryResolveToolApproval(string toolCallId, ToolApprovalDecision decision) =>
        _pendingApprovals.TryGetValue(toolCallId, out var pending)
        && pending.Completion.TrySetResult(decision);

    private void OnToolApprovalDecisionReceived(object? sender, ToolApprovalDecisionEventArgs e) =>
        TryResolveToolApproval(e.ToolCallId, e.Decision);

    private void ShowPendingApprovals()
    {
        _ = RestorePendingToolApprovalsAsync();
    }

    public async Task RestorePendingToolApprovalsAsync()
    {
        foreach (var pending in _pendingApprovals.Values)
        {
            await ShowToolApprovalAsync(pending).ConfigureAwait(true);
        }
    }

    private Task ShowToolApprovalAsync(PendingUiApproval pending) =>
        RunOnUiTaskAsync(() => ChatView?.ShowToolApprovalAsync(pending.Approval, pending.Arguments)
            ?? Task.CompletedTask);

    private Task ResolveToolApprovalAsync(string toolCallId, ToolApprovalDecision decision) =>
        RunOnUiTaskAsync(() => ChatView?.ResolveToolApprovalAsync(toolCallId, decision)
            ?? Task.CompletedTask);

    private sealed record PendingUiApproval(
        PendingToolApproval Approval,
        string Arguments,
        TaskCompletionSource<ToolApprovalDecision> Completion);
}
