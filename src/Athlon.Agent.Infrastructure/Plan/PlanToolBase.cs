using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.Plan;

internal static class PlanToolBase
{
    public static ToolResult MissingSession() =>
        ToolResult.Failure("No session", "No active agent session. Cannot use plan tools.");

    public static string? RequireSessionId(IActiveAgentSessionContext sessionContext) =>
        string.IsNullOrWhiteSpace(sessionContext.SessionId) ? null : sessionContext.SessionId;
}
