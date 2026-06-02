using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

public sealed class OpenAiCompatibleChatModelClient(
    HttpClient httpClient,
    IAppLogger logger,
    AppSettings settings,
    ICredentialStore credentialStore,
    ISessionHttpLogService sessionHttpLog,
    IActiveAgentSessionContext activeSessionContext) : IAgentModelClient
{
    private readonly IAppLogger _logger = logger.ForContext("ModelGateway");

    public async Task<AgentModelResponse> CompleteAsync(
        AgentModelRequest request,
        Func<string, Task>? onTextDelta = null,
        Func<string, Task>? onReasoningDelta = null,
        Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await credentialStore.GetSecretAsync(ModelSettings.ApiKeySecretName, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(settings.Model.LegacyApiKeyCredentialName))
        {
            apiKey = await credentialStore.GetSecretAsync(settings.Model.LegacyApiKeyCredentialName, cancellationToken);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                await credentialStore.SaveSecretAsync(ModelSettings.ApiKeySecretName, apiKey, cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("模型 API Key 未配置。请在 Settings > Model 中输入 API Key 并保存，或设置环境变量 OPENAI_API_KEY。");
        }

        var preferStreaming = settings.Model.EnableStreaming;

        if (preferStreaming)
        {
            try
            {
                return await CompleteOpenAiCompatibleAsync(request, apiKey, stream: true, onTextDelta, onReasoningDelta, onToolCallDelta, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(
                    "Streaming completion failed, fallback to non-stream mode: {Message} (AllowToolCalls={AllowToolCalls})",
                    ex.Message,
                    request.AllowToolCalls);
            }
        }

        return await CompleteOpenAiCompatibleAsync(request, apiKey, stream: false, onTextDelta, onReasoningDelta, onToolCallDelta, cancellationToken);
    }

    private async Task<AgentModelResponse> CompleteOpenAiCompatibleAsync(
        AgentModelRequest request,
        string apiKey,
        bool stream,
        Func<string, Task>? onTextDelta,
        Func<string, Task>? onReasoningDelta,
        Func<StreamingToolCallDelta, Task>? onToolCallDelta,
        CancellationToken cancellationToken)
    {
        var endpoint = settings.Model.Endpoint.TrimEnd('/') + "/chat/completions";
        var purpose = !request.AllowToolCalls && request.MaxTokens.HasValue ? "context-summary" : "chat-completion";
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

        var sessionId = activeSessionContext.SessionId;
        var sw = Stopwatch.StartNew();
        string? responseBody = null;
        int? statusCode = null;
        string? error = null;

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await httpClient.SendAsync(
                httpRequest,
                stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                _logger.Warning(
                    "Model HTTP failed {StatusCode} for session {SessionId}: {Body}",
                    statusCode,
                    sessionId,
                    HttpLogSanitizer.Truncate(responseBody));
                throw new HttpRequestException($"{error}. Body: {HttpLogSanitizer.Truncate(responseBody)}");
            }

            if (stream)
            {
                return await ParseStreamingResponseAsync(response, onTextDelta, onReasoningDelta, onToolCallDelta, body => responseBody = body, cancellationToken);
            }

            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return await EmitParsedResponseAsync(responseBody, onTextDelta, onReasoningDelta, onToolCallDelta);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error ??= ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
            try
            {
                await sessionHttpLog.LogInteractionAsync(
                    sessionId,
                    new SessionHttpInteractionLog(
                        DateTimeOffset.UtcNow,
                        endpoint,
                        purpose,
                        statusCode,
                        payload,
                        responseBody,
                        error,
                        sw.ElapsedMilliseconds),
                    CancellationToken.None);
            }
            catch (Exception logEx) when (logEx is not OperationCanceledException)
            {
                _logger.Warning(
                    "Failed to write HTTP interaction log for session {SessionId}: {Message}",
                    sessionId ?? "(none)",
                    logEx.Message);
            }
        }
    }

    private static AgentModelResponse ParseNonStreamingResponse(string responseBody)
    {
        using var json = JsonDocument.Parse(responseBody);
        var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;

        var toolCalls = ParseToolCallsFromMessage(message);
        var reasoningContent = TryReadReasoningContent(message);
        return NormalizeAssistantResponse(content, toolCalls, reasoningContent);
    }

    private static async Task<AgentModelResponse> EmitParsedResponseAsync(
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

    private static AgentModelResponse NormalizeAssistantResponse(
        string content,
        IReadOnlyList<AgentToolCall> toolCalls,
        string? reasoningContent)
    {
        var (normalizedContent, normalizedReasoning) = SplitEmbeddedThinkingContent(content, reasoningContent);
        return new AgentModelResponse(normalizedContent, toolCalls, normalizedReasoning);
    }

    /// <summary>
    /// Qwen 等模型会把思考链嵌入 content（如 &lt;/redacted_thinking&gt;），而非 reasoning_content 字段。
    /// </summary>
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

    private async Task<AgentModelResponse> ParseStreamingResponseAsync(
        HttpResponseMessage response,
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
                _logger.Information(
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
                            : toolCalls.Count;
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
            _logger.Warning("Streaming response did not contain SSE data lines, fallback to JSON body parsing.");
            var body = fallbackBuilder.ToString().Trim();
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
        return NormalizeAssistantResponse(contentBuilder.ToString(), normalizedToolCalls, reasoningContent);
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
                    required = tool.Parameters.Where(parameter => !parameter.Value.StartsWith("Optional", StringComparison.OrdinalIgnoreCase)).Select(parameter => parameter.Key).ToArray()
                }
            }
        };
    }

    private static IReadOnlyDictionary<string, string> ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Dictionary<string, string>();
        }

        using var json = JsonDocument.Parse(argumentsJson);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }

        var arguments = json.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText());
        return ToolPathNormalizer.NormalizePathArguments(arguments);
    }
}
