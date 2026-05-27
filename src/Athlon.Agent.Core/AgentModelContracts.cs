using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public sealed record AgentModelMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<AgentToolCall>? ToolCalls = null);
public sealed record AgentToolCall(
    string Id,
    string Name,
    IReadOnlyDictionary<string, string> Arguments);
public sealed record AgentModelRequest(
    IReadOnlyList<AgentModelMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    bool AllowToolCalls = true,
    int? MaxTokens = null);
public sealed record AgentModelResponse(
    string Content,
    IReadOnlyList<AgentToolCall> ToolCalls);
public interface IAgentModelClient
{
    Task<AgentModelResponse> CompleteAsync(
        AgentModelRequest request,
        Func<string, Task>? onTextDelta = null,
        CancellationToken cancellationToken = default);
}
