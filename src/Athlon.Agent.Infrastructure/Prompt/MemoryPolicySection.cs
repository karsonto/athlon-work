using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

/// <summary>Guides the model to use session-scoped memory tools when a workspace is active.</summary>
public sealed class MemoryPolicySection : IEnvironmentPromptSection
{
    public string Name => "workflow:memory";

    public int Order => PromptSectionBands.WorkflowStart + 6;

    public PromptSectionPlacement Placement => PromptSectionPlacement.PreCall;

    public void Append(System.Text.StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context)
            || string.IsNullOrWhiteSpace(context.WorkspaceRoot)
            || !PromptModeHelper.HasAny(context, "memory_search", "memory_get"))
        {
            return;
        }

        builder.AppendLine("Project session memory:");
        builder.AppendLine("- Long-term memory is scoped to the current workspace and this conversation session. It stores durable facts. The live task skeleton is session_note_read, not memory.");
        builder.AppendLine("- memory_search reads curated memory only. Archived conversation transcripts are not memory files; use the history transcript tools for those. There is no memory write tool; do not use memory in place of session_note_append.");
        if (PromptModeHelper.HasTool(context, "memory_search"))
        {
            builder.AppendLine("- Call memory_search before answering questions about preferences, people, stable decisions, or deadlines. Do not use it for the current goal or next step.");
        }

        if (PromptModeHelper.HasTool(context, "memory_get"))
        {
            builder.AppendLine("- Use memory_get to read full context around matched lines (path relative to the session memory directory, e.g. MEMORY.md or 2026-04-01.md).");
        }

        builder.AppendLine();
    }
}
