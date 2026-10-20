using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Threading;

namespace Athlon.Agent.Infrastructure.Harness;

public sealed class TaskListPromptContributor(
    ISessionTaskListStore taskListStore,
    IAgentRunContextAccessor runContextAccessor) : IRuntimeContextContributor
{
    public int Priority => 35;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        var sessionId = runContextAccessor.Current?.SessionId ?? context.Session.Id;
        var list = SyncOverAsync.Run(() => taskListStore.GetAsync(sessionId));
        if (list.Items.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("## Current Task List");
        builder.AppendLine();
        builder.AppendLine("Your persisted todo list is shown below. Execute against it this turn (follow Long-task discipline).");
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
