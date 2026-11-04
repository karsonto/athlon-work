using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

/// <summary>
/// Guides the model to use session-scoped memory tools when a workspace is active, and the
/// account-level habit profile whenever <c>profile_get</c> is both advertised and enabled.
/// </summary>
public sealed class MemoryPolicySection(AppSettings settings) : IEnvironmentPromptSection
{
    public string Name => "workflow:memory";

    public int Order => PromptSectionBands.WorkflowStart + 6;

    public PromptSectionPlacement Placement => PromptSectionPlacement.PreCall;

    public void Append(System.Text.StringBuilder builder, EnvironmentPromptContext context)
    {
        var profileAdvertised = settings.Profile.Enabled
            && context.Tools.Any(
                tool => string.Equals(tool.Name, "profile_get", StringComparison.OrdinalIgnoreCase));

        if (!PromptModeHelper.IsChatOnly(context) && !string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            builder.AppendLine("Project session memory:");
            builder.AppendLine("- Long-term memory is scoped to the current workspace and this conversation session. It stores durable facts. The live task skeleton is session_note_read, not memory.");
            builder.AppendLine("- When memory_search is advertised, it reads curated memory only. Archived conversation transcripts are not memory files; use the history transcript tools for those. There is no memory write tool; do not use memory in place of session_note_append.");
            builder.AppendLine("- When memory_search is advertised, call it before answering questions about preferences, people, stable decisions, or deadlines. Do not use it for the current goal or next step.");
            builder.AppendLine("- When memory_get is advertised, use it to read full context around matched lines (path relative to the session memory directory, e.g. MEMORY.md or 2026-04-01.md).");
            builder.AppendLine();
        }

        if (profileAdvertised)
        {
            builder.AppendLine("Account profile:");
            builder.AppendLine("- profile_get reads the account-level profile, shared across every workspace and session: user habits (how they want to be addressed, language, tone, response format) and lessons distilled from past corrections. It may come from a remote store and can lag slightly.");
            builder.AppendLine("- When profile_get is advertised, call it at the start of a conversation, whenever tone or output format matters, or before reusing an approach that previously went wrong. Read before assuming a preference.");
            builder.AppendLine("- The profile is maintained automatically after each turn; there is no write tool. When the user states a lasting preference, just acknowledge it normally.");
            builder.AppendLine("- profile_get is not session memory: memory_search / memory_get only cover project facts for the current session. Do not mix the two.");
            builder.AppendLine();
        }
    }
}
