using System.Text;

using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class HarnessPlanningSection : IEnvironmentPromptSection
{
    public string Name => "workflow:harness-planning";

    public int Order => PromptSectionBands.WorkflowStart + 5;

    public PromptSectionPlacement Placement => PromptSectionPlacement.PreCall;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context)
            || !PromptModeHelper.HasTool(context, "todo_write"))
        {
            return;
        }

        builder.AppendLine("Long-task discipline:");
        builder.AppendLine("- For multi-file, multi-step, or architectural work: explore first (grep_files, glob_files, file_read when advertised), then call todo_write with the COMPLETE list.");
        builder.AppendLine("- Choose the merge flag from what already exists, not from habit: if the task list is empty, the first write uses merge=false; if the list already exists (including a list pre-seeded from an approved plan), always use merge=true and update items by id. Never use merge=false on a non-empty list unless the user asked you to replace it.");
        builder.AppendLine("- Keep each todo content actionable and verifiable; at most one todo in_progress; mark completed only after its verification command passes.");
        builder.AppendLine("- If scope grows mid-work, update the todo list (merge=true) before continuing edits.");
        builder.AppendLine("- Your current todo list is re-injected every reasoning turn; treat it as the execution checklist.");
        builder.AppendLine("- Do not use todo_write for trivial single-step requests, pure questions, or chit-chat.");
        builder.AppendLine();
    }
}
