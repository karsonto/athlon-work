using System.Text.Json;

namespace Athlon.Agent.Core;

public static class AssistantToolCallsCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string? Serialize(IReadOnlyList<AgentToolCall> toolCalls)
    {
        if (toolCalls.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(toolCalls, Options);
    }

    public static IReadOnlyList<AgentToolCall>? Deserialize(string? toolCallsJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize<List<AgentToolCall>>(toolCallsJson, Options);
    }
}
