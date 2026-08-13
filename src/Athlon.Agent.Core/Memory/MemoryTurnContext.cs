namespace Athlon.Agent.Core.Memory;

public sealed record MemoryTurnContext(
    IReadOnlyList<ChatMessage> Messages,
    string? EnvironmentPrompt = null,
    IReadOnlyList<ToolDefinition>? Tools = null);
