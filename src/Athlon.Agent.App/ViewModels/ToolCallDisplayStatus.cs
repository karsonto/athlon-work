namespace Athlon.Agent.App.ViewModels;

public enum ToolCallDisplayStatus
{
    None,
    Preparing,
    Running,
    AwaitingApproval,
    ApprovalDenied,
    Succeeded,
    Failed,
    Cancelled
}
