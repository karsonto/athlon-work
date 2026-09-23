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
        builder.AppendLine("Coordinate rules (apply to every observation result):");
        builder.AppendLine("- Pass image_x/image_y in screenshot pixels: (0..image.width-1, 0..image.height-1). Use the center of a node's image_bounds when the target is visible.");
        builder.AppendLine("- Never pass UI tree `bounds` (those are physical desktop pixels) as image_x/image_y, and never multiply by dpi_scale. The host maps screenshot pixels to the physical desktop.");
        builder.AppendLine("- Physical x/y and end_x/end_y are a fallback for when only physical geometry is known.");
        builder.AppendLine("- For a pointer action, if both element_id and image_x/image_y are provided, the pixel coordinates win.");
        builder.AppendLine("Rules:");
        builder.AppendLine("1. Start with computer_observe and use the returned screenshot plus image.width/image.height.");
        builder.AppendLine("2. Perform exactly one computer_interact action at a time.");
        builder.AppendLine("3. Prefer element_id as the target: the host can act through the control itself without moving the pointer. Use image_x/image_y when the target is canvas-drawn, custom, or not present in the UI tree; right_click and drag require pixel coordinates.");
        builder.AppendLine("4. Never reuse a stale frame_id. On stale_frame, off_monitor, or element_gone, call computer_observe again — do not blindly retry the same coordinates.");
        builder.AppendLine("5. Use computer_wait for asynchronous UI changes instead of fixed sleeps.");
        builder.AppendLine("6. Verify every action from the post-action screenshot before continuing. The result includes a fresh frame id and screenshot; call computer_observe when you need a new UI tree.");
        builder.AppendLine("7. Do not claim completion until the visible desktop state confirms the requested result.");
        builder.AppendLine("8. To work on a display or app other than the one under the cursor, pass monitor_index or window_title to computer_observe instead of moving the pointer there first. Installed UI Automation patterns handle click/type_text/scroll on elements; element_id is frame-scoped, so prefer automation_id (when present) for cross-frame references.");
    }
}
