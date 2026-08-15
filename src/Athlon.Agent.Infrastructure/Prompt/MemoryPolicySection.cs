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
        builder.AppendLine("- Long-term memory is scoped to the current workspace and this conversation session.");
        if (PromptModeHelper.HasTool(context, "memory_search"))
        {
            builder.AppendLine("- Call memory_search before answering questions about past work, preferences, or decisions in this session.");
        }

        if (PromptModeHelper.HasTool(context, "memory_get"))
        {
            builder.AppendLine("- Use memory_get to read full context around matched lines (path relative to the session memory directory, e.g. MEMORY.md or 2026-04-01.md).");
        }

        builder.AppendLine();
    }
}
