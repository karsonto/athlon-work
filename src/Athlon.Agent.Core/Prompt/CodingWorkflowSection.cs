using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class CodingWorkflowSection : IEnvironmentPromptSection
{
    public int Order => 410;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context) || PromptModeHelper.IsAskMode(context))
        {
            return;
        }

        builder.AppendLine("Coding workflow:");
        builder.AppendLine("- Requirements: First read and understand the user's request thoroughly. If anything is ambiguous, missing, or unclear, ask the user for clarification before proceeding. Do not start planning or editing code until the goal, constraints, and success criteria are confirmed.");
        builder.AppendLine("- Planning: for multi-step or multi-file tasks, explore with grep_files, glob_files, and file_read first; state a brief plan before editing.");
        builder.AppendLine("- Verification: after file_write, file_edit, or apply_patch, run execute_command to verify with project-appropriate checks (e.g. mvn -q -pl <module> compile, npx tsc --noEmit, ruff check <path>, pytest <test file>).");
        builder.AppendLine("- Run only tests related to your changes, not the full suite. Treat command output as ground truth; fix root causes and re-run until checks pass before claiming completion.");
        builder.AppendLine("- Standards: read before editing; make minimal focused changes; fix root causes; match existing style; do not fix unrelated issues.");
        builder.AppendLine("- Persistence: keep working until the current task is verified, not merely edited.");
        builder.AppendLine();
    }
}
