using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure;

public sealed class BackgroundCommandPromptContributor(
    BackgroundCommandRegistry backgroundCommands,
    IAgentRunContextAccessor runContextAccessor) : IRuntimeContextContributor
{
    public int Priority => 32;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        var sessionId = runContextAccessor.Current?.SessionId ?? context.Session.Id;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var completions = backgroundCommands.DrainCompletions(sessionId);
        if (completions.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("## Background command completions (system reminder)");
        builder.AppendLine();
        foreach (var completion in completions)
        {
            builder.AppendLine(completion.AnnounceText);
            builder.AppendLine();
        }
    }
}
