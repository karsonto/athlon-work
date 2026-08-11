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
        builder.AppendLine("2. Perform exactly one computer_interact action at a time. Prefer element_id; use physical coordinates only as fallback.");
        builder.AppendLine("3. Never reuse a stale frame_id. Observe again after any unexpected change or when interact reports stale_frame.");
        builder.AppendLine("4. Use computer_wait for asynchronous UI changes instead of fixed sleeps.");
        builder.AppendLine("5. Verify every action from the post-action screenshot before continuing.");
        builder.AppendLine("6. Do not claim completion until the visible desktop state confirms the requested result.");
    }
}
