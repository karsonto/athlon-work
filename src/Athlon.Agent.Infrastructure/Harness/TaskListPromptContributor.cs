using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Harness;

public sealed class TaskListPromptContributor(
    ISessionHarnessState harnessState,
    ISessionTaskListStore taskListStore,
    IAgentRunContextAccessor runContextAccessor) : IPreReasoningPromptContributor
{
    public int Priority => 35;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!harnessState.IsEnabledForActiveRun(runContextAccessor) || PromptModeHelper.IsChatOnly(context))
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
