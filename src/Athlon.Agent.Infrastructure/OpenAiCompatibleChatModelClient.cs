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

        var preferStreaming = settings.Model.EnableStreaming
            && onTextDelta is not null
            && request.AllowToolCalls;

        if (preferStreaming)
        {
            try
            {
                return await CompleteOpenAiCompatibleAsync(request, apiKey, stream: true, onTextDelta, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning("Streaming completion failed, fallback to non-stream mode: {Message}", ex.Message);
            }
        }

        return await CompleteOpenAiCompatibleAsync(request, apiKey, stream: false, onTextDelta, cancellationToken);
    }

    private async Task<AgentModelResponse> CompleteOpenAiCompatibleAsync(
        AgentModelRequest request,
        string apiKey,
        bool stream,
        Func<string, Task>? onTextDelta,
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

        if (request.MaxTokens is > 0)
        {
            payload["max_tokens"] = request.MaxTokens.Value;
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
                return await ParseStreamingResponseAsync(response, onTextDelta, body => responseBody = body, cancellationToken);
            }

            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var nonStreamResult = ParseNonStreamingResponse(responseBody);
            if (onTextDelta is not null && !string.IsNullOrEmpty(nonStreamResult.Content))
            {
                await onTextDelta(nonStreamResult.Content);
            }

            return nonStreamResult;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error ??= ex.Message;
            throw;
        }
        finally
        {
            sw.Stop();
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
    }

    private static AgentModelResponse ParseNonStreamingResponse(string responseBody)
    {
        using var json = JsonDocument.Parse(responseBody);
        var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString() ?? string.Empty
            : string.Empty;

        var toolCalls = ParseToolCallsFromMessage(message);
        return new AgentModelResponse(content, toolCalls);
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

    private static async Task<AgentModelResponse> ParseStreamingResponseAsync(
        HttpResponseMessage response,
        Func<string, Task>? onTextDelta,
        Action<string> setResponseBody,
        CancellationToken cancellationToken)
    {
        var contentBuilder = new StringBuilder();
        var rawBuilder = new StringBuilder();
        var toolCalls = new Dictionary<int, StreamingToolCallState>();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(data))
            {
                continue;
            }

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
                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (delta.TryGetProperty("content", out var token) && token.ValueKind == JsonValueKind.String)
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

                if (delta.TryGetProperty("tool_calls", out var deltaToolCalls) && deltaToolCalls.ValueKind == JsonValueKind.Array)
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

                            if (function.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.String)
                            {
                                state.Arguments.Append(argsElement.GetString());
                            }
                        }
                    }
                }
            }
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

        return new AgentModelResponse(contentBuilder.ToString(), normalizedToolCalls);
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

        return json.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText());
    }
}
