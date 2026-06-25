using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class HarnessPlanningSection(ISessionHarnessState harnessState) : IEnvironmentPromptSection
{
    public int Order => 405;

    public PromptSectionPlacement Placement => PromptSectionPlacement.PreCall;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!harnessState.IsEnabled(context.Session.Id) || PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        builder.AppendLine("Harness planning (this session has Harness enabled):");
        builder.AppendLine("- For multi-step or multi-file tasks, call todo_write with the COMPLETE list of steps before editing.");
        builder.AppendLine("- Mark a todo completed only after its verification command passes.");
        builder.AppendLine("- Your current todo list is re-injected every reasoning turn; treat it as the source of truth.");
        builder.AppendLine("- Do not use todo_write for trivial single-step requests, pure questions, or chit-chat.");
        builder.AppendLine("- Long-term memory tools (memory_search, memory_get) are available to recall facts from prior sessions.");
        builder.AppendLine();
    }
}
