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
            builder.AppendLine("- Tool decision tree:");
            builder.AppendLine("  1. Mode gate: Ask mode permits read-only tools only; use file_read, grep_files, glob_files, file_list, memory_search, memory_get, and knowledge_search when available.");
            builder.AppendLine("  2. Reject mutation: do NOT call file_write, file_edit, apply_patch, execute_command, or sub-agent tools (sessions_*, task_output).");
            builder.AppendLine("  3. Execute independent read-only calls in parallel; otherwise preserve dependency order.");
            AppendMcpDecisionFlow(builder);
            builder.AppendLine("- If the same tool fails with the same error twice, stop repeating it; gather more context or switch tools.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("- Native tools via function calling; use each tool's schema.");
        builder.AppendLine("- Tool decision tree:");
        builder.AppendLine("  1. Inspect with the narrowest native read tool whose schema matches the need; do not guess file contents.");
        builder.AppendLine("  2. Run independent read-only calls (file_read, grep_files, glob_files, file_list, memory_search) in parallel; preserve dependency order and never mix writes or execute_command into that round.");
        builder.AppendLine("  3. Before file_write, file_edit, or apply_patch, explain the intended write.");
        builder.AppendLine("  4. Shell: cmd.exe only, not PowerShell; quote paths with spaces or non-ASCII and source workspace paths from tool results.");
        builder.AppendLine("- Skill scripts: use absolute paths from each skill's files-root; execute_command cwd defaults to workspace root.");
        AppendMcpDecisionFlow(builder);
        builder.AppendLine("- If the same tool fails with the same error twice, stop repeating it; gather more context or switch tools.");
        builder.AppendLine();
    }

    private static void AppendMcpDecisionFlow(StringBuilder builder)
    {
        builder.AppendLine("- MCP tools (when present) are advertised only via function schemas.");
        builder.AppendLine("- MCP decision flow:");
        builder.AppendLine("  1. If a concrete MCP tool is directly advertised, call it using its schema.");
        builder.AppendLine("  2. If mcp_search is advertised, search by user intent and inspect the top-ranked results.");
        builder.AppendLine("  3. When a search result says requiresDescribe=false, its inputSchema is complete and mcp_call may be used directly.");
        builder.AppendLine("  4. When requiresDescribe=true or schemaTruncated=true, call mcp_describe first and follow the complete schema.");
        builder.AppendLine("  5. Call mcp_call with a native arguments object; never pass argumentsJson or JSON-stringify arguments.");
        builder.AppendLine("  6. Re-search or re-describe when the schema fingerprint changes or validation reports schema drift.");
    }
}
