using System.Text;

namespace Athlon.Agent.Core.Prompt;

/// <summary>Guidance for the parent agent on when and how to delegate via sessions_* tools.</summary>
public sealed class SubAgentDelegationSection(AppSettings settings) : IEnvironmentPromptSection
{
    public int Order => 550;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!settings.SubAgent.Enabled)
        {
            return;
        }

        builder.AppendLine("## Delegating sub-tasks");
        builder.AppendLine("Use `sessions_spawn` / `sessions_send` for structured sub-agent orchestration.");
        builder.AppendLine("- **New child:** `sessions_spawn` with `role` (who the child is, boundaries, output style), optional `message`, optional `label` for reuse.");
        builder.AppendLine("- **Continue:** `sessions_send` with `session_key` or `label` and a new `message`.");
        builder.AppendLine("- **Discover:** `sessions_list` when you do not remember session_key; `sessions_history` for transcript snippets.");
        builder.AppendLine("- **Long tasks:** `timeout_seconds=0` returns `task_id`; next turn call `sessions_pending_completions` or wait for system reminder injection; use `task_output` to poll.");
        builder.AppendLine("- You may name a skill in `message` or let the child use `load_skill_through_path` from the skills list.");
        builder.AppendLine("- Wait for tool results; summarize for the user. Children cannot spawn nested agents.");
        builder.AppendLine();
    }
}
