using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

/// <summary>Guides the model to use session-scoped memory tools when a workspace is active.</summary>
public sealed class MemoryPolicySection : IEnvironmentPromptSection
{
    public int Order => 406;

    public PromptSectionPlacement Placement => PromptSectionPlacement.PreCall;

    public void Append(System.Text.StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context) || string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            return;
        }

        builder.AppendLine("Project session memory:");
        builder.AppendLine("- Long-term memory is scoped to the current workspace and this conversation session.");
        builder.AppendLine("- Call memory_search before answering questions about past work, preferences, or decisions in this session.");
        builder.AppendLine("- Use memory_get to read full context around matched lines (path relative to the session memory directory, e.g. MEMORY.md or 2026-04-01.md).");
        builder.AppendLine();
    }
}
