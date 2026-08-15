using System.Text;



namespace Athlon.Agent.Core.Prompt;



public sealed class CodingWorkflowSection : IEnvironmentPromptSection

{

    public string Name => "workflow:coding";



    public int Order => PromptSectionBands.WorkflowStart + 10;



    public void Append(StringBuilder builder, EnvironmentPromptContext context)

    {

        if (PromptModeHelper.IsChatOnly(context)

            || PromptModeHelper.IsAskMode(context)

            || PromptModeHelper.IsPlanMode(context))

        {

            return;

        }



        builder.AppendLine("Coding workflow:");

        builder.AppendLine("- Requirements: First read and understand the user's request thoroughly. If anything is ambiguous, missing, or unclear, ask the user for clarification before proceeding.");

        if (PromptModeHelper.IsCodingMode(context))

        {

            if (PromptModeHelper.HasTool(context, "todo_write"))

            {

                builder.AppendLine("- Planning: for multi-step or multi-file tasks, explore first; if an approved Session Plan is injected, follow it; otherwise use todo_write for structured steps before editing.");

            }

            else

            {

                builder.AppendLine("- Planning: for multi-step or multi-file tasks, explore first; if an approved Session Plan is injected, follow it.");

            }

        }

        else if (PromptModeHelper.HasAny(context, "grep_files", "glob_files", "file_read"))

        {

            builder.AppendLine("- Planning: for multi-step or multi-file tasks, explore with advertised read/search tools first; state a brief plan before editing.");

        }

        else

        {

            builder.AppendLine("- Planning: for multi-step or multi-file tasks, explore first; state a brief plan before editing.");

        }



        if (PromptModeHelper.HasAny(context, "file_write", "file_edit", "apply_patch")

            && PromptModeHelper.HasTool(context, "execute_command"))

        {

            builder.AppendLine("- Verification: after file_write, file_edit, or apply_patch, run execute_command to verify with project-appropriate checks (e.g. mvn -q -pl <module> compile, npx tsc --noEmit, ruff check <path>, pytest <test file>).");

            builder.AppendLine("- Run only tests related to your changes, not the full suite. Treat command output as ground truth; fix root causes and re-run until checks pass before claiming completion.");

        }



        builder.AppendLine("- Standards: read before editing; make minimal focused changes; fix root causes; match existing style; do not fix unrelated issues.");

        builder.AppendLine("- Persistence: keep working until the current task is verified, not merely edited. For long tasks, keep todos accurate across turns when todo_write is advertised.");

        builder.AppendLine();

    }

}

