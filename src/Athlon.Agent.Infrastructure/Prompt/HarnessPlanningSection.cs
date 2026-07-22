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

        builder.AppendLine("Coding long-task discipline:");
        builder.AppendLine("- For multi-file, multi-step, or architectural work: explore first (grep_files, glob_files, file_read), then call todo_write with the COMPLETE list (merge=false on first write; merge=true later to update by id).");
        builder.AppendLine("- If an approved Session Plan is present in context, treat it as the source of truth; do not deviate without updating todos (or ask the user to return to Plan mode for spec changes).");
        builder.AppendLine("- Keep each todo content actionable and verifiable; at most one todo in_progress; mark completed only after its verification command passes.");
        builder.AppendLine("- If scope grows mid-work, update the todo list (merge=true) before continuing edits.");
        builder.AppendLine("- Your current todo list is re-injected every reasoning turn; treat it as the execution checklist.");
        builder.AppendLine("- Do not use todo_write for trivial single-step requests, pure questions, or chit-chat.");
        builder.AppendLine("- A prior Session Plan is optional — direct Coding without Plan is fine.");
        builder.AppendLine();
    }
}
