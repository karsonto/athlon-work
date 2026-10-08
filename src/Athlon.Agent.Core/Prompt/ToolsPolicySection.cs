using System.Text;



namespace Athlon.Agent.Core.Prompt;



public sealed class ToolsPolicySection : IEnvironmentPromptSection

{

    public string Name => "tools:decision-tree";



    public int Order => PromptSectionBands.ToolGuidanceStart + 50;



    public void Append(StringBuilder builder, EnvironmentPromptContext context)

    {

        builder.AppendLine("Tools:");



        if (context.Tools.Count > 0

            && context.Tools.All(tool => string.Equals(tool.Source, "computer-use", StringComparison.Ordinal)))

        {

            builder.AppendLine("Computer Use is the exclusive runtime tool mode for this turn.");

            builder.AppendLine("Only computer_observe, computer_interact, and computer_wait may be called.");

            builder.AppendLine("Observe first, perform one action, then verify from the returned screenshot.");

            builder.AppendLine("Do not attempt native workspace, shell, browser, memory, knowledge, MCP, skill, plan, todo, or sub-agent tools.");

            builder.AppendLine();

            return;

        }



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



        if (PromptModeHelper.IsAskMode(context) || PromptModeHelper.IsPlanMode(context))

        {

            builder.AppendLine("- Tool decision tree:");

            builder.AppendLine("  1. Mode gate: read-only tools only; use advertised readers (file_*, grep_files, glob_files, memory_*, knowledge_*) when present.");

            if (PromptModeHelper.IsPlanMode(context))
            {
                builder.AppendLine("  2. Call publish_plan when the active phase says to publish; do not edit project files or run shell.");
                builder.AppendLine("  3. Reject other mutation: do not call write/patch/shell/sub-agent tools.");
            }
            else
            {
                builder.AppendLine("  2. Reject mutation: do not call write/patch/shell/terminal/MCP/sub-agent tools even if you remember them from other modes.");
            }

            builder.AppendLine(PromptModeHelper.IsPlanMode(context)
                ? "  4. Execute independent read-only calls in parallel; otherwise preserve dependency order."
                : "  3. Execute independent read-only calls in parallel; otherwise preserve dependency order.");

            builder.AppendLine("- If the same tool fails with the same error twice, stop repeating it; gather more context or switch tools.");

            builder.AppendLine();

            return;

        }



        builder.AppendLine("- Native tools via function calling; use each tool's schema.");

        builder.AppendLine("- Tool decision tree:");

        builder.AppendLine("  1. Inspect with the narrowest native read tool whose schema matches the need; do not guess file contents.");

        builder.AppendLine("  2. Consecutive read-only calls may be sent together; keep writes and execute_command in their own groups, and preserve dependency order.");

        builder.AppendLine("  3. When todo_write is advertised, multi-step / multi-file work: maintain an accurate todo list via todo_write (create or merge) before and during writes.");

        builder.AppendLine("  4. When file_write, file_edit, or apply_patch are advertised, explain the intended write before calling them.");

        builder.AppendLine("  5. Shell: when execute_command is advertised, cmd.exe only, not PowerShell; quote paths with spaces or non-ASCII and source workspace paths from tool results.");

        builder.AppendLine("- When execute_command is advertised, skill scripts use absolute paths from each skill's files-root; execute_command cwd defaults to workspace root.");
        builder.AppendLine("- When execute_command is advertised, block_until_ms defaults to 30000 and only returns control. Servers and watch use 0. Builds and tests that may run longer stay in the background; read them with command_await or the completion reminder. timeout still stops the process.");

        builder.AppendLine("- Interactive CLI agents in the workspace Terminal tab: when terminal_open, terminal_send_input, or terminal_read_output are advertised, see runtime context for terminal_* rules; use execute_command only for one-off non-interactive shell commands.");

        builder.AppendLine("- Browser tab tools: when browser_navigate, browser_find_aria_nodes, or browser_network_list are advertised, see runtime context for ARIA find → act → verify rules and DevTools network/console tools.");

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
        builder.AppendLine("  3. When mcp_describe or mcp_call are advertised and a search result says requiresDescribe=false, its inputSchema is complete and mcp_call may be used directly.");
        builder.AppendLine("  4. When requiresDescribe=true or schemaTruncated=true, call mcp_describe first and follow the complete schema.");
        builder.AppendLine("  5. Call mcp_call with a native arguments object; never pass argumentsJson or JSON-stringify arguments.");
        builder.AppendLine("  6. Re-search or re-describe when the schema fingerprint changes or validation reports schema drift.");
    }

}

