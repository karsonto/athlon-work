using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class WorkspacePolicySection : IEnvironmentPromptSection
{
    public int Order => 300;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!context.HasWorkspace)
        {
            builder.AppendLine("当前工作区尚未设定。");
            builder.AppendLine("请让用户通过侧栏「配置」或设置页的 Workspace 指定工作区目录后，再使用 file_list、file_read、file_write、file_edit、grep_files、glob_files 等文件工具。");
            builder.AppendLine("在工作区未设定前，不要假设任何文件路径，也不要调用访问工作区文件的工具。");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("All relative file paths are resolved from the active workspace. Never access files outside the configured workspace.");
        builder.AppendLine("In file tool arguments (path), always use forward slashes (/), e.g. src/foo.cs — not backslashes (\\), even on Windows.");
        builder.AppendLine($"Active workspace: {context.WorkspaceName}");
        builder.AppendLine($"Workspace root: {context.WorkspaceRoot}");
        builder.AppendLine("Workspace contents are intentionally not embedded in this prompt because they change often.");
        builder.AppendLine("Use file_list to fetch a live directory listing when needed.");
        builder.AppendLine();
        AppendPlanningGuidance(builder, context);
        builder.AppendLine();
    }

    private static void AppendPlanningGuidance(StringBuilder builder, EnvironmentPromptContext context)
    {
        builder.AppendLine("Planning for multi-step or long-running tasks:");
        builder.AppendLine(
            "- For requests that span multiple turns, many files, or roughly more than 30 minutes of work, call create_plan first; do not attempt the entire scope in one turn.");
        builder.AppendLine(
            $"- Split work into granular subtasks (up to {context.PlanMaxSubtasks}): each subtask must be a smallest verifiable unit completable in one focused turn (e.g. add API + unit test), not vague goals like \"finish the module\".");
        builder.AppendLine(
            "- Name each subtask and fill description / expectedOutcome with concrete, measurable details (paths, types, commands, acceptance criteria); avoid vague phrases like \"improve code\" or \"polish feature\".");
        builder.AppendLine(
            "- Prefer more short subtasks over a few large ones; use create_plan to replace the plan if scope changes.");
        builder.AppendLine("- Use create_plan to define the plan and ordered subtasks (do not hand-write plan.md with file_write).");
        builder.AppendLine("- Execute one in-progress subtask at a time; after each step, call finish_subtask with a specific measurable outcome.");
        builder.AppendLine("- Use get_plan when you need the full current plan and subtask states.");
        builder.AppendLine("- plan.md is synced automatically by plan tools; do not edit checkboxes in plan.md with file_edit unless the user explicitly asks.");

        if (context.PlanAutoContinueEnabled)
        {
            builder.AppendLine(
                "- While a subtask is in progress, the client may auto-send a continue instruction when a turn ends; do not claim the overall task is complete until every subtask is done or abandoned.");
            builder.AppendLine(
                "- If the current subtask is still too large for one turn, split remaining work into smaller subtasks via create_plan before continuing.");
        }
    }
}
