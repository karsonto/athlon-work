namespace Athlon.Agent.Core.Compaction;

public interface IToolResultEvictor
{
    Task<string> EvictIfNeededAsync(
        string sessionId,
        AgentToolCall toolCall,
        ToolResult result,
        string formattedToolContent,
        CancellationToken cancellationToken = default);
}
