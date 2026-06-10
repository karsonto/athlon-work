using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal static class OpenAiChatRequestFactory
{
    public static Dictionary<string, object?> BuildPayload(AgentModelRequest request, AppSettings settings, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.Model.ModelName,
            ["stream"] = stream,
            ["messages"] = request.Messages.Select(ToOpenAiMessage).ToArray()
        };

        if (request.AllowToolCalls && request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.Select(ToOpenAiTool).ToArray();
            payload["tool_choice"] = "auto";
        }

        var maxTokens = request.MaxTokens is > 0
            ? request.MaxTokens
            : settings.Model.MaxTokens is > 0
                ? settings.Model.MaxTokens
                : null;
        if (maxTokens is > 0)
        {
            payload["max_tokens"] = maxTokens.Value;
        }

        return payload;
    }

    public static string BuildPurpose(AgentModelRequest request) =>
        !request.AllowToolCalls && request.MaxTokens.HasValue ? "context-summary" : "chat-completion";

    private static Dictionary<string, object?> ToOpenAiMessage(AgentModelMessage message)
    {
        var result = new Dictionary<string, object?>
        {
            ["role"] = message.Role,
            ["content"] = message.Content
        };

        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            result["tool_call_id"] = message.ToolCallId;
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            result["tool_calls"] = message.ToolCalls.Select(call => new
            {
                id = call.Id,
                type = "function",
                function = new
                {
                    name = call.Name,
                    arguments = JsonSerializer.Serialize(call.Arguments)
                }
            }).ToArray();
        }

        if (string.Equals(message.Role, "assistant", StringComparison.Ordinal)
            && message.ReasoningContent is not null)
        {
            result["reasoning_content"] = message.ReasoningContent;
        }

        return result;
    }

    private static object ToOpenAiTool(ToolDefinition tool)
    {
        var properties = tool.Parameters.ToDictionary(
            parameter => parameter.Key,
            parameter => (object)new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = parameter.Value
            });

        return new
        {
            type = "function",
            function = new
            {
                name = tool.Name,
                description = tool.Description,
                parameters = new
                {
                    type = "object",
                    properties,
                    required = tool.Parameters
                        .Where(parameter => !parameter.Value.StartsWith("Optional", StringComparison.OrdinalIgnoreCase))
                        .Select(parameter => parameter.Key)
                        .ToArray()
                }
            }
        };
    }
}
