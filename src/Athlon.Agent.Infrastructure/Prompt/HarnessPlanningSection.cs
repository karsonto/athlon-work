using System.Text;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class HarnessPlanningSection : IEnvironmentPromptSection
{
    public int Order => 405;

    public PromptSectionPlacement Placement => PromptSectionPlacement.PreCall;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!PromptModeHelper.IsCodingMode(context) || PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        builder.AppendLine("Coding planning:");
        builder.AppendLine("- For multi-file, multi-step, or architectural work: if the goal or success criteria are unclear, ask the user before calling todo_write.");
        builder.AppendLine("- Call todo_write with the COMPLETE list of verifiable steps before editing (merge=false on first write; merge=true later to update by id).");
        builder.AppendLine("- Keep each todo content actionable and verifiable; avoid walls of implementation detail.");
        builder.AppendLine("- Keep at most one todo in_progress; mark completed only after its verification command passes.");
        builder.AppendLine("- If scope grows mid-work, update the todo list before continuing edits.");
        builder.AppendLine("- Your current todo list is re-injected every reasoning turn; treat it as the source of truth.");
        builder.AppendLine("- Do not use todo_write for trivial single-step requests, pure questions, or chit-chat.");
        builder.AppendLine();
    }
}
