namespace Athlon.Agent.Core.Plan;

public static class PlanToolCatalog
{
    public const string CreatePlan = "create_plan";
    public const string FinishSubtask = "finish_subtask";
    public const string GetPlan = "get_plan";

    private static readonly HashSet<string> PlanToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        CreatePlan,
        FinishSubtask,
        GetPlan
    };

    private static readonly HashSet<string> MutatingNativeToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_write",
        "file_edit",
        "execute_command"
    };

    public static bool IsPlanTool(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && PlanToolNames.Contains(toolName);

    public static bool IsMutatingNativeTool(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && MutatingNativeToolNames.Contains(toolName);

    public static IReadOnlyList<ToolDefinition> FilterForSession(
        IReadOnlyList<ToolDefinition> tools,
        AgentInteractionMode mode,
        AgentPlan? plan)
    {
        if (mode == AgentInteractionMode.Plan)
        {
            return tools
                .Where(tool =>
                    !IsMutatingNativeTool(tool.Name)
                    && !string.Equals(tool.Name, FinishSubtask, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var filtered = tools.Where(tool => !IsPlanTool(tool.Name)).ToArray();
        if (plan?.Phase != PlanPhase.Approved)
        {
            return filtered;
        }

        var executionPlanTools = tools
            .Where(tool =>
                string.Equals(tool.Name, GetPlan, StringComparison.OrdinalIgnoreCase)
                || string.Equals(tool.Name, FinishSubtask, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return filtered.Concat(executionPlanTools)
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
