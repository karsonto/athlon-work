using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Chat;
using Athlon.Agent.Infrastructure.Chat;

namespace Athlon.Agent.Infrastructure;

/// <summary>
/// Facade over <see cref="IChatResponseDialect"/> for OpenAI-compatible HTTP bodies and SSE streams.
/// Provider-specific field / thinking-tag differences live in dialects, not here.
/// </summary>
internal static class OpenAiChatResponseParser
{
    private static readonly ChatResponseDialectRegistry Dialects = ChatResponseDialectRegistry.CreateDefault();

    public static AgentModelResponse ParseNonStreamingResponse(string responseBody)
    {
        using var json = JsonDocument.Parse(responseBody);
        var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");
        var dialect = Dialects.Resolve(message);
        var content = dialect.ReadContent(message);
        var toolCalls = MaterializeToolCalls(dialect.ReadToolCalls(message), idleTimedOut: false);
        var reasoningContent = dialect.ReadReasoning(message);
        var usage = TryParseUsage(json.RootElement);
        return NormalizeAssistantResponse(dialect, content, toolCalls, reasoningContent, usage);
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

        var streamDialect = Dialects.StreamingDefault;
        var contentBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var rawBuilder = new StringBuilder();
        var fallbackBuilder = new StringBuilder();
        var toolCalls = new Dictionary<int, StreamingToolCallState>();
        ModelUsage? usage = null;
        var sawSseData = false;
        var idleTimedOut = false;
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
                idleTimedOut = true;
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

                await AppendReasoningDelta(streamDialect, payload, reasoningBuilder, onReasoningDelta);

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

                        if (TryBuildToolCallDelta(index, state, out var delta))
                        {
                            await NotifyToolCallDeltaAsync(onToolCallDelta, delta, cancellationToken);
                        }
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
                var call = CreateToolCall(
                    IdGen.EnsureId(state.Id),
                    state.Name ?? string.Empty,
                    args,
                    idleTimedOut);
                if (idleTimedOut && call.ArgumentsParseError is not null)
                {
                    logger.Warning(
                        "Tool call {ToolName} arguments incomplete after streaming idle timeout "
                        + "(argsLength={ArgsLength}, startsWithBrace={StartsWithBrace}, endsWithBrace={EndsWithBrace}): {Error}",
                        call.Name,
                        args.Length,
                        args.StartsWith('{'),
                        args.EndsWith('}'),
                        call.ArgumentsParseError);
                }

                return call;
            })
            .ToArray();

        var content = contentBuilder.ToString();
        var reasoningContent = reasoningBuilder.Length == 0 ? null : reasoningBuilder.ToString();
        // Resolve dialect against the assembled message so embedded-thinking tags can still peel.
        var assembled = BuildAssembledMessageElement(content, reasoningContent);
        var dialect = Dialects.Resolve(assembled);
        return NormalizeAssistantResponse(dialect, content, normalizedToolCalls, reasoningContent, usage);
    }

    private static JsonElement BuildAssembledMessageElement(string content, string? reasoning)
    {
        using var doc = JsonDocument.Parse(
            reasoning is null
                ? $"{{\"content\":{JsonSerializer.Serialize(content)}}}"
                : $"{{\"content\":{JsonSerializer.Serialize(content)},\"reasoning_content\":{JsonSerializer.Serialize(reasoning)}}}");
        return doc.RootElement.Clone();
    }

    private static AgentModelResponse NormalizeAssistantResponse(
        IChatResponseDialect dialect,
        string content,
        IReadOnlyList<AgentToolCall> toolCalls,
        string? reasoningContent,
        ModelUsage? usage = null)
    {
        var (normalizedContent, normalizedReasoning) = dialect.Normalize(content, reasoningContent);
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

    private static List<AgentToolCall> MaterializeToolCalls(
        IEnumerable<ChatToolCallDraft> drafts,
        bool idleTimedOut)
    {
        var toolCalls = new List<AgentToolCall>();
        foreach (var draft in drafts)
        {
            toolCalls.Add(CreateToolCall(draft.Id, draft.Name, draft.ArgumentsJson, idleTimedOut));
        }

        return toolCalls;
    }

    /// <summary>
    /// Edit tools emit one path-only snapshot, then stay silent so later chunks are not copied.
    /// Other tools keep the accumulated arguments snapshot.
    /// </summary>
    private static bool TryBuildToolCallDelta(
        int index,
        StreamingToolCallState state,
        out StreamingToolCallDelta delta)
    {
        delta = null!;
        if (state.DisplayPathEmitted)
        {
            return false;
        }

        if (ToolCallStreamingJsonHelper.IsEditTool(state.Name))
        {
            var windowLength = Math.Min(state.Arguments.Length, ToolCallStreamingJsonHelper.EditPathScanWindow);
            if (windowLength == 0)
            {
                return false;
            }

            var window = state.Arguments.ToString(0, windowLength);
            if (!ToolCallStreamingJsonHelper.TryExtractEditDisplayPath(window, state.Name, out var path))
            {
                return false;
            }

            state.DisplayPathEmitted = true;
            delta = new StreamingToolCallDelta(
                index,
                state.Id,
                state.Name,
                ToolCallStreamingJsonHelper.PathOnlyArgumentsJson(path));
            return true;
        }

        delta = new StreamingToolCallDelta(index, state.Id, state.Name, state.Arguments.ToString());
        return true;
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
        public bool DisplayPathEmitted { get; set; }
    }

    private static async Task AppendReasoningDelta(
        IChatResponseDialect dialect,
        JsonElement delta,
        StringBuilder reasoningBuilder,
        Func<string, Task>? onReasoningDelta)
    {
        var tokenText = dialect.ReadReasoningDelta(delta);
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

    private static AgentToolCall CreateToolCall(
        string id,
        string name,
        string argumentsJson,
        bool idleTimedOut)
    {
        if (!ToolCallArguments.TryParse(argumentsJson, out var parsed, out var parseError))
        {
            var error = parseError ?? "Invalid tool arguments JSON.";
            if (idleTimedOut)
            {
                error = "Incomplete tool arguments JSON after streaming idle timeout. " + error;
            }

            return new AgentToolCall(id, name, ToolCallArguments.Empty)
            {
                RawArgumentsJson = argumentsJson,
                ArgumentsParseError = error
            };
        }

        return new AgentToolCall(id, name, ToolPathNormalizer.NormalizePathArguments(parsed))
        {
            RawArgumentsJson = argumentsJson
        };
    }
}
