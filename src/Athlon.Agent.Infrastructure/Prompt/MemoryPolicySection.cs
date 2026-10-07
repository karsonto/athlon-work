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
            || string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            return;
        }

        builder.AppendLine("Project session memory:");
        builder.AppendLine("- Long-term memory is scoped to the current workspace and this conversation session. It stores durable facts. The live task skeleton is session_note_read, not memory.");
        builder.AppendLine("- When memory_search is advertised, it reads curated memory only. Archived conversation transcripts are not memory files; use the history transcript tools for those. There is no memory write tool; do not use memory in place of session_note_append.");
        builder.AppendLine("- When memory_search is advertised, call it before answering questions about preferences, people, stable decisions, or deadlines. Do not use it for the current goal or next step.");
        builder.AppendLine("- When memory_get is advertised, use it to read full context around matched lines (path relative to the session memory directory, e.g. MEMORY.md or 2026-04-01.md).");

        builder.AppendLine();
    }
}
