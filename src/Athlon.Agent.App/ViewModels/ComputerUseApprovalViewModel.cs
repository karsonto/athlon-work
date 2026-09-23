namespace Athlon.Agent.App.ViewModels;

/// <summary>
/// A pending tool approval rendered inside the Computer Use overlay. The overlay deliberately has no
/// chat surface, so approvals are shown as a compact standalone card instead.
/// </summary>
public sealed class ComputerUseApprovalViewModel(
    string toolCallId,
    string toolName,
    string arguments)
{
    public string ToolCallId { get; } = toolCallId;

    public string ToolName { get; } = toolName;

    public string Arguments { get; } = arguments;
}
