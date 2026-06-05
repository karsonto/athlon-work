using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class ToolsPolicySection : IEnvironmentPromptSection
{
    public int Order => 500;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        builder.AppendLine("Tools:");
        builder.AppendLine(
            "Native tools are provided via function calling. Use each tool's schema. Do not guess file contents.");
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
        builder.AppendLine("Windows: cmd.exe only, not PowerShell. execute_command defaults cwd to the workspace root; use workspace-relative cwd when needed.");
        builder.AppendLine("Skill scripts: use absolute paths from each skill's <files-root> inside the command string; do not use workspace-relative paths for skill files.");
        builder.AppendLine("In cmd, quote paths that contain spaces or non-ASCII characters (e.g. type \"docs/报告.txt\").");
        builder.AppendLine("When a command references a workspace file, take the path verbatim from the latest file_list/glob_files tool result — not from paraphrased assistant text.");
        builder.AppendLine();
    }

    private static bool IsMcpTool(ToolDefinition tool) =>
        string.Equals(tool.Source, "mcp", StringComparison.OrdinalIgnoreCase);
}
