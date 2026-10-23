using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Chat;

namespace Athlon.Agent.Infrastructure.Chat;

/// <summary>
/// Standard OpenAI-compatible message shape: content, tool_calls, reasoning_content / reasoning.
/// </summary>
internal sealed class OpenAiCompatibleDialect : IChatResponseDialect
{
    public string Name => "openai-compatible";

    public int Priority => 0;

    public bool CanHandle(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object;

    public string ReadContent(JsonElement message)
    {
        if (message.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    public string? ReadReasoning(JsonElement message)
    {
        if (message.TryGetProperty("reasoning_content", out var reasoningContent)
            && reasoningContent.ValueKind == JsonValueKind.String)
        {
            return reasoningContent.GetString();
        }

        if (message.TryGetProperty("reasoning", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.String)
        {
            return reasoning.GetString();
        }

        return null;
    }

    public IEnumerable<ChatToolCallDraft> ReadToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var callsElement)
            || callsElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var call in callsElement.EnumerateArray())
        {
            var function = call.GetProperty("function");
            var id = call.GetProperty("id").GetString() ?? IdGen.NewId();
            var name = function.GetProperty("name").GetString() ?? string.Empty;
            var argumentsJson = function.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement.ValueKind switch
                {
                    JsonValueKind.String => argumentsElement.GetString() ?? "{}",
                    JsonValueKind.Object => argumentsElement.GetRawText(),
                    _ => "{}"
                }
                : "{}";
            yield return new ChatToolCallDraft(id, name, argumentsJson);
        }
    }

    public (string Content, string? Reasoning) Normalize(string content, string? reasoning) =>
        (content, reasoning);

    public string? ReadReasoningDelta(JsonElement payload) => ReadReasoning(payload);
}
