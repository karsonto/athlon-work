namespace Athlon.Agent.Core.Prompt;

public sealed class EnvironmentPromptContext
{
    public required AgentSession Session { get; init; }

    public string? WorkspaceRoot { get; init; }

    public string? WorkspaceName { get; init; }

    public IReadOnlyList<string> IgnorePatterns { get; init; } = [];

    public required IReadOnlyList<ToolDefinition> Tools { get; init; }

    public required string SkillsDirectory { get; init; }

    public required IAgentHostEnvironment Host { get; init; }

    public required PromptSettings PromptSettings { get; init; }

    public bool PlanAutoContinueEnabled { get; init; }

    public int PlanMaxSubtasks { get; init; } = 20;

    public bool HasWorkspace => !string.IsNullOrWhiteSpace(WorkspaceRoot);
}
