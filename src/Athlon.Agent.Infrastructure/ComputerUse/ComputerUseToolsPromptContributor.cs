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
        builder.AppendLine("1. Start with computer_observe and use the returned screenshot plus image.width/image.height.");
        builder.AppendLine("2. Perform exactly one computer_interact action at a time.");
        builder.AppendLine("3. For click, double_click, right_click, scroll, and drag: pass image_x/image_y (and end_image_* for drag) in screenshot pixels. Prefer the center of image_bounds when a node is visible. Never pass UI tree bounds as image_x/image_y. Never multiply by dpi_scale — the host maps screenshot pixels to the physical desktop.");
        builder.AppendLine("4. Use element_id only to focus a control for type_text/key/hotkey, or when you truly cannot see the target on the screenshot. If both element_id and image_x/image_y are set for a pointer action, image coordinates win.");
        builder.AppendLine("5. Never reuse a stale frame_id. On stale_frame, off_monitor, or element_gone, call computer_observe again — do not blindly retry the same coordinates.");
        builder.AppendLine("6. Use computer_wait for asynchronous UI changes instead of fixed sleeps.");
        builder.AppendLine("7. Verify every action from the post-action screenshot before continuing. The result includes a fresh frame id and screenshot; call computer_observe when you need a new UI tree.");
        builder.AppendLine("8. Do not claim completion until the visible desktop state confirms the requested result.");
    }
}
