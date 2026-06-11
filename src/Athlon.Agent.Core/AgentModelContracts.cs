using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public sealed record AgentModelMessage(
    string Role,
    object Content,
    string? ToolCallId = null,
    IReadOnlyList<AgentToolCall>? ToolCalls = null,
    string? ReasoningContent = null);
public sealed record AgentToolCall(
    string Id,
    string Name,
    IReadOnlyDictionary<string, string> Arguments);
public sealed record AgentModelRequest(
    IReadOnlyList<AgentModelMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    bool AllowToolCalls = true,
    int? MaxTokens = null);
public sealed record ModelUsage(
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null,
    int? PromptCacheHitTokens = null,
    int? PromptCacheMissTokens = null,
    int? CacheReadTokens = null,
    int? CacheCreationTokens = null,
    PromptCacheAvailability PromptCacheAvailability = PromptCacheAvailability.Unknown)
{
    public double? PromptCacheHitRate =>
        PromptCacheAvailability == PromptCacheAvailability.HitMiss
        && PromptCacheHitTokens is > 0 or 0
        && PromptCacheMissTokens is > 0 or 0
        && PromptCacheHitTokens + PromptCacheMissTokens > 0
            ? (double)PromptCacheHitTokens.Value / (PromptCacheHitTokens.Value + PromptCacheMissTokens.Value)
            : null;
}

public sealed record AgentModelResponse(
    string Content,
    IReadOnlyList<AgentToolCall> ToolCalls,
    string? ReasoningContent = null,
    ModelUsage? Usage = null);

public sealed record StreamingToolCallDelta(
    int Index,
    string? Id,
    string? Name,
    string ArgumentsJson);

public interface IAgentModelClient
{
    Task<AgentModelResponse> CompleteAsync(
        AgentModelRequest request,
        Func<string, Task>? onTextDelta = null,
        Func<string, Task>? onReasoningDelta = null,
        Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
        CancellationToken cancellationToken = default);
}
