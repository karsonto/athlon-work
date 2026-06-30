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

        if (PromptModeHelper.IsAskMode(context))
        {
            builder.AppendLine("- Ask mode: read-only tools only. Use file_read, grep_files, glob_files, file_list, and knowledge_search when available.");
            builder.AppendLine("- Do NOT call file_write, file_edit, apply_patch, execute_command, or sub-agent tools (sessions_*, task_output).");
            builder.AppendLine("- Read-only tools may run in parallel when they do not depend on each other.");
            builder.AppendLine("- MCP tools (when present) are advertised only via function schemas.");
            builder.AppendLine("- If the same tool fails with the same error twice, stop repeating it; gather more context or switch tools.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("- Native tools via function calling; use each tool's schema. Do not guess file contents.");
        builder.AppendLine("- Writes: explain intent before file_write, file_edit, or apply_patch.");
        builder.AppendLine("- Read-only tools (file_read, grep_files, glob_files, file_list, memory_search) may run in parallel when they do not depend on each other and no writes or execute_command appear in the same round.");
        builder.AppendLine("- Shell: cmd.exe only, not PowerShell; quote paths with spaces or non-ASCII; workspace file paths in commands must come from tool results, not paraphrased text.");
        builder.AppendLine("- Skill scripts: use absolute paths from each skill's files-root; execute_command cwd defaults to workspace root.");
        builder.AppendLine("- MCP tools (when present) are advertised only via function schemas; use mcp_search then mcp_call in search mode.");
        builder.AppendLine("- If the same tool fails with the same error twice, stop repeating it; gather more context or switch tools.");
        builder.AppendLine();
    }
}
