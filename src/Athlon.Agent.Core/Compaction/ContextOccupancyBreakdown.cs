namespace Athlon.Agent.Core.Compaction;

public sealed record ContextOccupancyBreakdown(
    int SystemPrompt = 0,
    int ToolDefinitions = 0,
    int Rules = 0,
    int Skills = 0,
    int McpTools = 0,
    int SubagentDefinitions = 0,
    int Conversation = 0)
{
    public static ContextOccupancyBreakdown Empty { get; } = new();

    public int ContentTokens =>
        SystemPrompt + ToolDefinitions + Rules + Skills + McpTools + SubagentDefinitions + Conversation;
}
