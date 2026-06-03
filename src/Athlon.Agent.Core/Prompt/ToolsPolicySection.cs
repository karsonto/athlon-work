using System.Text;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Core.Prompt;

public sealed class ToolsPolicySection : IEnvironmentPromptSection
{
    public int Order => 500;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        builder.AppendLine("Tools:");

        if (context.InteractionMode == AgentInteractionMode.Plan)
        {
            builder.AppendLine(
                "Native tools are provided via function calling (read-only file tools plus create_plan and get_plan for structured planning). "
                + "Use each tool's schema. Do not guess file contents.");
        }
        else if (context.ActivePlan?.Phase == PlanPhase.Approved)
        {
            builder.AppendLine(
                "Native tools are provided via function calling (including get_plan and finish_subtask for the approved plan). "
                + "Use each tool's schema. Do not guess file contents.");
        }
        else
        {
            builder.AppendLine(
                "Native tools are provided via function calling. Use each tool's schema. Do not guess file contents.");
        }

        builder.AppendLine();

        var mcpTools = context.Tools.Where(IsMcpTool).ToArray();
        if (mcpTools.Length > 0)
        {
            builder.AppendLine("Available MCP tools:");
            foreach (var tool in mcpTools)
            {
                builder.AppendLine($"- {tool.Name}: {tool.Description}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("For write operations, explain your intent before calling file_write or file_edit.");
        builder.AppendLine("Windows: cmd.exe only, not PowerShell.");
        builder.AppendLine();
    }

    private static bool IsMcpTool(ToolDefinition tool) =>
        string.Equals(tool.Source, "mcp", StringComparison.OrdinalIgnoreCase);
}
