using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Harness;

public sealed class TaskListPromptContributor(
    ISessionHarnessState harnessState,
    ISessionTaskListStore taskListStore,
    IAgentRunContextAccessor runContextAccessor) : IRuntimeContextContributor
{
    public int Priority => 35;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!harnessState.IsCodingModeForActiveRun(runContextAccessor) || PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        var sessionId = runContextAccessor.Current?.SessionId ?? context.Session.Id;
        var list = taskListStore.GetAsync(sessionId).GetAwaiter().GetResult();
        if (list.Items.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("## Current Task List");
        builder.AppendLine();
        builder.AppendLine("Your persisted todo list is shown below. Re-read it every turn and keep statuses accurate.");
        builder.AppendLine("- Focus this turn on the current in_progress item (at most one).");
        builder.AppendLine("- Mark completed only after verification passes.");
        builder.AppendLine("- If scope changes, call todo_write before further edits.");
        builder.AppendLine("- If an approved Session Plan is in context, keep work aligned with it.");
        builder.AppendLine();
        foreach (var item in list.Items)
        {
            builder.Append("- [")
                .Append(item.Status)
                .Append("] ")
                .Append(item.Id)
                .Append(": ")
                .AppendLine(item.Content);
        }

        builder.AppendLine();
    }
}
