using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class PlanModePolicySection : IEnvironmentPromptSection
{
    public int Order => 310;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (context.InteractionMode != AgentInteractionMode.Plan)
        {
            return;
        }

        builder.AppendLine("Plan mode (spec-first workflow):");
        builder.AppendLine(
            "- You are in Plan mode: research and specify before implementation. Do not write files, edit files, or run commands.");
        builder.AppendLine(
            "- Separate what to build from building it. The user approves the plan via the Build button before any execution.");
        builder.AppendLine();
        builder.AppendLine("When to create a plan:");
        builder.AppendLine(
            "- Complex features with multiple approaches; tasks touching many files or systems; unclear requirements; architectural decisions.");
        builder.AppendLine(
            "- For small, obvious one-file fixes, a full plan is optional — still answer concisely.");
        builder.AppendLine();
        builder.AppendLine("Workflow:");
        builder.AppendLine(
            "- Research first: use file_list, file_read, grep_files, glob_files to understand the codebase; do not guess file contents.");
        builder.AppendLine(
            "- Clarify when needed: if requirements are ambiguous, ask the user focused questions before locking the plan.");
        builder.AppendLine(
            $"- Create structured plan: call create_plan with ordered, granular subtasks (up to {context.PlanMaxSubtasks}). "
            + "Each subtask must be the smallest verifiable unit with concrete paths, types, commands, and acceptance criteria.");
        builder.AppendLine("- Do not hand-write plan.md with file_write; plan tools sync it automatically.");
        builder.AppendLine("- Use get_plan to review the current draft plan and subtask states.");
        builder.AppendLine(
            "- Do not call finish_subtask in Plan mode. Do not implement the feature — tell the user to review the plan in the editor and click Build to execute.");
        builder.AppendLine();
        builder.AppendLine("After the plan is ready:");
        builder.AppendLine(
            "- Summarize the plan briefly and ask the user to review plan.md in the editor (or get_plan output) and click Build in the editor header when ready to execute.");
        builder.AppendLine();
    }
}
