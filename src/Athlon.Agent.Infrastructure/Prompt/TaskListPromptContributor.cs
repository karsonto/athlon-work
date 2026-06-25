using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class TaskListPromptContributor(
    ISessionTaskStore taskStore) : IPreReasoningPromptContributor
{
    public int Priority => 35;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        var sessionId = context.Session.Id;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var tasks = taskStore.LoadAsync(sessionId).GetAwaiter().GetResult();
        if (tasks.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("<system-reminder>");
        builder.AppendLine("Current task list (source of truth for remaining work):");
        foreach (var task in tasks)
        {
            builder.AppendLine($"- [{task.Status}] {task.Id}: {task.Content}");
        }

        builder.AppendLine("</system-reminder>");
        builder.AppendLine();
    }
}
