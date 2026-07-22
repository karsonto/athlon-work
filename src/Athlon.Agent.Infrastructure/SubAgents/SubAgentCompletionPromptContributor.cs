using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SubAgentCompletionPromptContributor(
    AppSettings settings,
    Lazy<ISubAgentSessionManager> sessionManager,
    IAgentRunContextAccessor runContextAccessor) : IRuntimeContextContributor
{
    public int Priority => 30;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!settings.SubAgent.Enabled || PromptModeHelper.IsChatOnly(context) || PromptModeHelper.IsAskMode(context)
            || PromptModeHelper.IsPlanMode(context))
        {
            return;
        }

        if (runContextAccessor.Current?.Kind == AgentRunKind.SubAgent)
        {
            return;
        }

        var parentSessionId = runContextAccessor.Current?.SessionId ?? context.Session.Id;
        var completions = sessionManager.Value.DrainCompletionsAsync(parentSessionId, settings.SubAgent.MaxPendingCompletionsPerParent)
            .GetAwaiter()
            .GetResult();
        if (completions.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("## Sub-agent completions (system reminder)");
        builder.AppendLine();
        foreach (var completion in completions)
        {
            builder.AppendLine(completion.AnnounceText);
            builder.AppendLine();
        }
    }
}
