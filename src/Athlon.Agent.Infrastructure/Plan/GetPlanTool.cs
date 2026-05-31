using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Infrastructure.Plan;

public sealed class GetPlanTool(IPlanNotebook planNotebook, IActiveAgentSessionContext sessionContext) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "get_plan",
        "View the current session plan as markdown, including subtask status.",
        new Dictionary<string, string>
        {
            ["detailed"] = "Optional: true/false for detailed subtask fields (default true)"
        });

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sessionId = PlanToolBase.RequireSessionId(sessionContext);
        if (sessionId is null)
        {
            return Task.FromResult(PlanToolBase.MissingSession());
        }

        var detailed = ParseDetailed(invocation);
        var markdown = planNotebook.GetPlanMarkdown(sessionId, detailed);
        var hasPlan = planNotebook.GetCurrent(sessionId) is not null;

        return Task.FromResult(
            hasPlan
                ? ToolResult.Success("Current plan", markdown)
                : ToolResult.Success(markdown, markdown));
    }

    private static bool ParseDetailed(ToolInvocation invocation)
    {
        if (!invocation.Arguments.TryGetValue("detailed", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return !bool.TryParse(value, out var parsed) || parsed;
    }
}
