using System.Diagnostics;
using System.Net.Http.Json;
using Athlon.Agent.Core;

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
        var apiKey = await ModelApiKeyResolver.ResolveAsync(credentialStore, settings, cancellationToken);
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
        var purpose = OpenAiChatRequestFactory.BuildPurpose(request);
        var payload = OpenAiChatRequestFactory.BuildPayload(request, settings, stream);

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
                    sessionId ?? "(none)",
                    HttpLogSanitizer.Truncate(responseBody) ?? string.Empty);
                throw new HttpRequestException($"{error}. Body: {HttpLogSanitizer.Truncate(responseBody)}");
            }

            if (stream)
            {
                return await OpenAiChatResponseParser.ParseStreamingResponseAsync(
                    response,
                    settings,
                    _logger,
                    onTextDelta,
                    onReasoningDelta,
                    onToolCallDelta,
                    body => responseBody = body,
                    cancellationToken);
            }

            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return await OpenAiChatResponseParser.EmitParsedResponseAsync(responseBody, onTextDelta, onReasoningDelta, onToolCallDelta);
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
}
