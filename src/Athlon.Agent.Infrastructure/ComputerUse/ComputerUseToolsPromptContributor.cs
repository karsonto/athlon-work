using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.ComputerUse;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.ComputerUse;

public sealed class ComputerUseToolsPromptContributor(
    IAgentRunContextAccessor runContextAccessor) : IComputerUseRuntimeContextContributor
{
    public int Priority => 1;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (runContextAccessor.Current?.ComputerUseActive != true)
        {
            return;
        }

        builder.AppendLine("Computer Use mode is active. Only computer_observe, computer_interact, and computer_wait are available.");
        builder.AppendLine("Rules:");
        builder.AppendLine("1. Start with computer_observe and use the returned screenshot and UI Automation element ids.");
        builder.AppendLine("2. Perform exactly one computer_interact action at a time. Prefer element_id; otherwise use image_x/image_y relative to the frame screenshot. Do not invent physical desktop pixels.");
        builder.AppendLine("3. Never reuse a stale frame_id. On stale_frame, off_monitor, or element_gone, call computer_observe again — do not blindly retry the same coordinates.");
        builder.AppendLine("4. Use computer_wait for asynchronous UI changes instead of fixed sleeps.");
        builder.AppendLine("5. Verify every action from the post-action screenshot before continuing. Post-action results omit the full UI tree; call computer_observe when you need fresh element ids.");
        builder.AppendLine("6. Do not claim completion until the visible desktop state confirms the requested result.");
    }
}
