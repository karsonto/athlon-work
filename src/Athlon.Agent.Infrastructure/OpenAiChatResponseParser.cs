using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal static class OpenAiChatResponseParser
{
    private static readonly string[] EmbeddedThinkingEndTags =
    [
        "\u003c/redacted_thinking\u003e",
        "\u0060/think\u0060"
    ];

    private static readonly string[] EmbeddedThinkingStartTags =
    [
        "\u003credacted_thinking\u003e",
        "\u0060think\u0060"
    ];

    public static AgentModelResponse ParseNonStreamingResponse(string responseBody)
    {
        using var json = JsonDocument.Parse(responseBody);
        var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;

        var toolCalls = ParseToolCallsFromMessage(message);
        var reasoningContent = TryReadReasoningContent(message);
        var usage = TryParseUsage(json.RootElement);
        return NormalizeAssistantResponse(content, toolCalls, reasoningContent, usage);
    }

    public static async Task<AgentModelResponse> EmitParsedResponseAsync(
        string responseBody,
        Func<string, Task>? onTextDelta,
        Func<string, Task>? onReasoningDelta,
        Func<StreamingToolCallDelta, Task>? onToolCallDelta = null)
    {
        var result = ParseNonStreamingResponse(responseBody);
        if (onReasoningDelta is not null && !string.IsNullOrEmpty(result.ReasoningContent))
        {
            await onReasoningDelta(result.ReasoningContent);
        }

        if (onTextDelta is not null && !string.IsNullOrEmpty(result.Content))
        {
            await onTextDelta(result.Content);
        }

        if (onToolCallDelta is not null)
        {
            for (var index = 0; index < result.ToolCalls.Count; index++)
            {
                var call = result.ToolCalls[index];
                var argumentsJson = JsonSerializer.Serialize(call.Arguments);
                await onToolCallDelta(new StreamingToolCallDelta(index, call.Id, call.Name, argumentsJson));
            }
        }

        return result;
    }

    public static async Task<AgentModelResponse> ParseStreamingResponseAsync(
        HttpResponseMessage response,
        AppSettings settings,
        IAppLogger logger,
        Func<string, Task>? onTextDelta,
        Func<string, Task>? onReasoningDelta,
        Func<StreamingToolCallDelta, Task>? onToolCallDelta,
        Action<string> setResponseBody,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(mediaType)
            && !string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            setResponseBody(body);
            return await EmitParsedResponseAsync(body, onTextDelta, onReasoningDelta, onToolCallDelta);
        }

        var contentBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var rawBuilder = new StringBuilder();
        var fallbackBuilder = new StringBuilder();
        var toolCalls = new Dictionary<int, StreamingToolCallState>();
        ModelUsage? usage = null;
        var sawSseData = false;
        var idleTimeoutSeconds = Math.Max(1, settings.Model.StreamingIdleTimeoutSeconds);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (true)
        {
            string? line;
            try
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idleCts.CancelAfter(TimeSpan.FromSeconds(idleTimeoutSeconds));
                line = await reader.ReadLineAsync(idleCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.Information(
                    "Streaming read idle timeout reached ({IdleTimeoutSeconds}s), finishing with partial content.",
                    idleTimeoutSeconds);
                break;
            }

            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                fallbackBuilder.AppendLine(line);
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(data))
            {
                continue;
            }

            sawSseData = true;
            rawBuilder.AppendLine(data);
            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                break;
            }

            using var chunk = JsonDocument.Parse(data);
            usage ??= TryParseUsage(chunk.RootElement);
            if (!chunk.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (!TryGetStreamPayload(choice, out var payload))
                {
                    continue;
                }

                if (payload.TryGetProperty("content", out var token) && token.ValueKind == JsonValueKind.String)
                {
                    var tokenText = token.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(tokenText))
                    {
                        contentBuilder.Append(tokenText);
                        if (onTextDelta is not null)
                        {
                            await onTextDelta(tokenText);
                        }
                    }
                }

                await AppendReasoningDelta(payload, reasoningBuilder, onReasoningDelta);

                if (payload.TryGetProperty("tool_calls", out var deltaToolCalls) && deltaToolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var partial in deltaToolCalls.EnumerateArray())
                    {
                        var index = partial.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsedIndex)
                            ? parsedIndex
                            : -(toolCalls.Count + 1);  // 唯一负索引，防止碰撞
                        if (!toolCalls.TryGetValue(index, out var state))
                        {
                            state = new StreamingToolCallState();
                            toolCalls[index] = state;
                        }

                        if (partial.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                        {
                            state.Id = idElement.GetString() ?? state.Id;
                        }

                        if (partial.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
                        {
                            if (function.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                            {
                                state.Name = nameElement.GetString() ?? state.Name;
                            }

                            if (function.TryGetProperty("arguments", out var argsElement))
                            {
                                switch (argsElement.ValueKind)
                                {
                                    case JsonValueKind.String:
                                        state.Arguments.Append(argsElement.GetString());
                                        break;
                                    case JsonValueKind.Object:
                                    case JsonValueKind.Array:
                                        state.Arguments.Clear();
                                        state.Arguments.Append(argsElement.GetRawText());
                                        break;
                                }
                            }
                        }

                        await NotifyToolCallDeltaAsync(
                            onToolCallDelta,
                            new StreamingToolCallDelta(
                                index,
                                state.Id,
                                state.Name,
                                state.Arguments.ToString()),
                            cancellationToken);
                    }
                }
            }
        }

        if (!sawSseData && fallbackBuilder.Length > 0)
        {
            logger.Warning("Streaming response did not contain SSE data lines, fallback to JSON body parsing.");
            var body = fallbackBuilder.ToString().Trim();
            // Skip any non-JSON prefix (HTTP headers, etc.) before the first '{'
            var jsonStart = body.IndexOf('{');
            if (jsonStart > 0)
            {
                body = body[jsonStart..];
            }
            setResponseBody(body);
            return await EmitParsedResponseAsync(body, onTextDelta, onReasoningDelta, onToolCallDelta);
        }

        setResponseBody(rawBuilder.ToString());
        var normalizedToolCalls = toolCalls
            .OrderBy(item => item.Key)
            .Select(item =>
            {
                var state = item.Value;
                var args = state.Arguments.Length == 0 ? "{}" : state.Arguments.ToString();
                return new AgentToolCall(
                    string.IsNullOrWhiteSpace(state.Id) ? Guid.NewGuid().ToString("N") : state.Id,
                    state.Name ?? string.Empty,
                    ParseArguments(args));
            })
            .ToArray();

        var reasoningContent = reasoningBuilder.Length == 0 ? null : reasoningBuilder.ToString();
        return NormalizeAssistantResponse(contentBuilder.ToString(), normalizedToolCalls, reasoningContent, usage);
    }

    private static AgentModelResponse NormalizeAssistantResponse(
        string content,
        IReadOnlyList<AgentToolCall> toolCalls,
        string? reasoningContent,
        ModelUsage? usage = null)
    {
        var (normalizedContent, normalizedReasoning) = SplitEmbeddedThinkingContent(content, reasoningContent);
        return new AgentModelResponse(normalizedContent, toolCalls, normalizedReasoning, usage);
    }

    private static ModelUsage? TryParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int? promptTokens = usageElement.TryGetProperty("prompt_tokens", out var promptElement)
                            && promptElement.TryGetInt32(out var promptValue)
            ? promptValue
            : null;
        int? completionTokens = usageElement.TryGetProperty("completion_tokens", out var completionElement)
                                && completionElement.TryGetInt32(out var completionValue)
            ? completionValue
            : null;
        int? totalTokens = usageElement.TryGetProperty("total_tokens", out var totalElement)
                           && totalElement.TryGetInt32(out var totalValue)
            ? totalValue
            : null;

        if (promptTokens is null && completionTokens is null && totalTokens is null)
        {
            return null;
        }

        var baseUsage = new ModelUsage(promptTokens, completionTokens, totalTokens);
        return PromptCacheUsageParser.MergeInto(baseUsage, usageElement);
    }

    private static (string Content, string? ReasoningContent) SplitEmbeddedThinkingContent(
        string content,
        string? reasoningContent)
    {
        if (!string.IsNullOrWhiteSpace(reasoningContent))
        {
            return (content, reasoningContent);
        }

        foreach (var endTag in EmbeddedThinkingEndTags)
        {
            var index = content.IndexOf(endTag, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var reasoning = content[..index].Trim();
            var answer = content[(index + endTag.Length)..].Trim();
            foreach (var startTag in EmbeddedThinkingStartTags)
            {
                if (reasoning.StartsWith(startTag, StringComparison.OrdinalIgnoreCase))
                {
                    reasoning = reasoning[startTag.Length..].Trim();
                    break;
                }
            }

            return (answer, string.IsNullOrWhiteSpace(reasoning) ? null : reasoning);
        }

        return (content, null);
    }

    private static List<AgentToolCall> ParseToolCallsFromMessage(JsonElement message)
    {
        var toolCalls = new List<AgentToolCall>();
        if (message.TryGetProperty("tool_calls", out var callsElement) && callsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in callsElement.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var id = call.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N");
                var name = function.GetProperty("name").GetString() ?? string.Empty;
                var argumentsJson = function.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.String
                    ? argumentsElement.GetString() ?? "{}"
                    : "{}";
                toolCalls.Add(new AgentToolCall(id, name, ParseArguments(argumentsJson)));
            }
        }

        return toolCalls;
    }

    private static async Task NotifyToolCallDeltaAsync(
        Func<StreamingToolCallDelta, Task>? onToolCallDelta,
        StreamingToolCallDelta delta,
        CancellationToken cancellationToken)
    {
        if (onToolCallDelta is null)
        {
            return;
        }

        await onToolCallDelta(delta).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool TryGetStreamPayload(JsonElement choice, out JsonElement payload)
    {
        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            payload = delta;
            return true;
        }

        if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            payload = message;
            return true;
        }

        payload = default;
        return false;
    }

    private sealed class StreamingToolCallState
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    private static string? TryReadReasoningContent(JsonElement message)
    {
        if (message.TryGetProperty("reasoning_content", out var reasoningContent)
            && reasoningContent.ValueKind == JsonValueKind.String)
        {
            return reasoningContent.GetString();
        }

        if (message.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
        {
            return reasoning.GetString();
        }

        return null;
    }

    private static async Task AppendReasoningDelta(
        JsonElement delta,
        StringBuilder reasoningBuilder,
        Func<string, Task>? onReasoningDelta)
    {
        string? tokenText = null;
        if (delta.TryGetProperty("reasoning_content", out var reasoningContent)
            && reasoningContent.ValueKind == JsonValueKind.String)
        {
            tokenText = reasoningContent.GetString();
        }
        else if (delta.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
        {
            tokenText = reasoning.GetString();
        }

        if (string.IsNullOrEmpty(tokenText))
        {
            return;
        }

        reasoningBuilder.Append(tokenText);
        if (onReasoningDelta is not null)
        {
            await onReasoningDelta(tokenText);
        }
    }

    private static IReadOnlyDictionary<string, string> ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            using var json = JsonDocument.Parse(argumentsJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>();
            }

            var arguments = json.RootElement.EnumerateObject()
                .GroupBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Last().Value.ValueKind == JsonValueKind.String
                        ? g.Last().Value.GetString() ?? string.Empty
                        : g.Last().Value.GetRawText(),
                    StringComparer.Ordinal);
            return ToolPathNormalizer.NormalizePathArguments(arguments);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
