namespace Athlon.Agent.Core.Plan;

/// <summary>
/// Builds the hidden user message that nudges the model to keep executing an approved plan.
///
/// <para>Used by the auto-continuation loop when the session task list still has open items but
/// the turn ended. The marker lets the UI hide the control message, following the same convention
/// as <see cref="ApprovedPlanPrompt"/> and
/// <see cref="Athlon.Agent.Core.SubAgents.SubAgentAutoContinuePrompt"/>.</para>
/// </summary>
public static class PlanContinuePrompt
{
    public const string Marker = "<athlon-plan-continue />";

    public static string BuildUserMessage() =>
        "You stopped before finishing the approved Session Plan.\n\n"
        + Marker
        + "\n\n"
        + "Resume the plan now: pick the current in_progress task (or the next pending one), finish and verify it, "
        + "update the task list with todo_write (merge=true), then continue to the next task. "
        + "Do not ask the user for permission to continue and do not re-plan. "
        + "Only stop when every task is completed, you are genuinely blocked, or you need a decision only the user can make.";

    public static bool IsPlanContinueMessage(ChatMessage message) =>
        message.Role == MessageRole.User
        && message.Content.Contains(Marker, StringComparison.Ordinal);
}
