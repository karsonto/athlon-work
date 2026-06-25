using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class ToolsPolicySection : IEnvironmentPromptSection
{
    public int Order => 500;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        builder.AppendLine("Tools:");

        if (PromptModeHelper.IsChatOnly(context))
        {
            if (PromptModeHelper.HasKnowledgeTool(context))
            {
                builder.AppendLine("Only knowledge_search is available. Use it to search knowledge modules enabled for this session.");
                builder.AppendLine("If no results are found, tell the user honestly.");
            }
            else
            {
                builder.AppendLine("No tools are available in this session. Answer directly; do not attempt function calling.");
            }

            builder.AppendLine();
            return;
        }

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

        builder.AppendLine("For write operations, explain your intent before calling file_write, file_edit, or apply_patch.");
        builder.AppendLine("If the same tool fails with the same error twice, stop repeating it; gather more context (search, file_read) or switch tools (e.g. apply_patch).");
        builder.AppendLine("Read-only tools (file_read, grep_files, glob_files, file_list, memory_search) may be called in parallel when they do not depend on each other and appear as the only tools in the same model round; if the round also includes writes or execute_command, all tools run sequentially.");
        builder.AppendLine("Windows: cmd.exe only, not PowerShell. execute_command defaults cwd to the workspace root; use workspace-relative cwd when needed.");
        builder.AppendLine("Skill scripts: use absolute paths from each skill's <files-root> inside the command string; do not use workspace-relative paths for skill files.");
        builder.AppendLine("In cmd, quote paths that contain spaces or non-ASCII characters (e.g. type \"docs/报告.txt\").");
        builder.AppendLine("When a command references a workspace file, take the path verbatim from the latest file_list/glob_files tool result — not from paraphrased assistant text.");
        builder.AppendLine();
    }

    private static bool IsMcpTool(ToolDefinition tool) =>
        string.Equals(tool.Source, "mcp", StringComparison.OrdinalIgnoreCase);
}
