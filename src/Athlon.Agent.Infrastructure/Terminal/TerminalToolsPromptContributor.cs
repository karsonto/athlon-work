using System.Text;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Terminal;

namespace Athlon.Agent.Infrastructure.Terminal;

public sealed class TerminalToolsPromptContributor(ITerminalWorkspaceState terminalWorkspaceState) : IRuntimeContextContributor
{
    public int Priority => 46;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsAskMode(context) || PromptModeHelper.IsPlanMode(context))
        {
            return;
        }

        if (terminalWorkspaceState.HasOpenTerminalTab)
        {
            builder.AppendLine("Workspace Terminal tools are available for the open Terminal tab.");
            builder.AppendLine("Rules:");
            builder.AppendLine("1. Use terminal_send_input to write to the same ConPTY session shown in the Terminal tab.");
            builder.AppendLine("2. append_newline defaults to true and sends Enter so prompts/commands submit; use text=\"\" with append_newline=true to press Enter only.");
            builder.AppendLine("3. Use terminal_read_output to read recent terminal output; poll again if the CLI is still working.");
            builder.AppendLine("4. Do not use execute_command for interactive CLI agents in the Terminal tab — it runs a separate cmd process.");
            builder.AppendLine("5. Prefer send → read → send for multi-step CLI agent conversations.");
            return;
        }

        builder.AppendLine(
            "No Terminal tab is open yet. Call terminal_open to open the workspace terminal; after it succeeds, "
            + "terminal_send_input and terminal_read_output unlock on the next model step in the same turn.");
    }
}
