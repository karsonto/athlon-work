using System.Text;

namespace Athlon.Agent.Core.Prompt;

/// <summary>Guidance for the parent agent on when and how to delegate via sessions_* tools.</summary>
public sealed class SubAgentDelegationSection(AppSettings settings) : IEnvironmentPromptSection
{
    public string Name => "tool:subagent-delegation";

    public int Order => PromptSectionBands.ToolGuidanceStart + 100;

    public PromptOccupancyKind OccupancyKind => PromptOccupancyKind.Subagent;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!settings.SubAgent.Enabled
            || PromptModeHelper.IsChatOnly(context)
            || PromptModeHelper.IsAskMode(context)
            || PromptModeHelper.IsPlanMode(context)
            || PromptModeHelper.IsDebugMode(context))
        {
            return;
        }

        builder.AppendLine("## Delegating sub-tasks");
        builder.AppendLine("Use `sessions_spawn` / `sessions_send` when they are advertised for structured sub-agent orchestration.");
        builder.AppendLine("- **New child:** when sessions_spawn is advertised, call it with `role` (who the child is, boundaries, output style), optional `message`, optional `label` for reuse.");
        builder.AppendLine("- **Continue:** when sessions_send is advertised, call it with `session_key` or `label` and a new `message`.");
        builder.AppendLine("- **Discover:** when sessions_list is advertised and you do not remember session_key, use it; sessions_history is for transcript snippets when advertised.");
        builder.AppendLine("- **Long tasks:** when sessions_pending_completions or task_output are advertised, `timeout_seconds=0` returns `task_id`; next turn call `sessions_pending_completions` or wait for system reminder injection; use `task_output` to poll.");

        builder.AppendLine("- You may name a skill in `message` or let the child use `load_skill_through_path` from the skills list.");
        builder.AppendLine("- Wait for tool results; summarize for the user. Children cannot spawn nested agents.");
        builder.AppendLine();
    }
}
