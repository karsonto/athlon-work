using System.Diagnostics;
using System.Net.Http.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Infrastructure;

public sealed class OpenAiCompatibleChatModelClient(
    HttpClient httpClient,
    IAppLogger logger,
    AppSettings settings,
    ICredentialStore credentialStore,
    ISessionHttpLogService sessionHttpLog,
    IActiveAgentSessionContext activeSessionContext,
    IAgentRunContextAccessor? runContextAccessor = null,
    IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null) : IAgentModelClient
{
    private readonly IAppLogger _logger = logger.ForContext("ModelGateway");
    private readonly IAgentRunContextAccessor? _runContextAccessor = runContextAccessor;
    private readonly IRuntimeDiagnosticEventSink? _runtimeDiagnosticEventSink = runtimeDiagnosticEventSink;

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
                await EnqueueDiagnosticAsync(
                    sessionId: activeSessionContext.SessionId,
                    component: RuntimeDiagnosticComponent.Model,
                    phase: RuntimeDiagnosticPhase.Streaming,
                    eventType: "model.streaming_interrupted",
                    severity: RuntimeDiagnosticSeverity.Warning,
                    errorCode: RuntimeDiagnosticErrorCodes.ModelStreamingInterrupted,
                    message: ex.Message).ConfigureAwait(false);
            }
        }

        return await CompleteOpenAiCompatibleAsync(request, apiKey, stream: false, onTextDelta, onReasoningDelta, onToolCallDelta, cancellationToken);
    }

    private async Task<AgentModelResponse> CompleteOpenAiCompatibleAsync(
        AgentModelRequest request,
        string? apiKey,
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
            httpRequest.Headers.TryAddWithoutValidation("User-Agent", "Athlon-Agent");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                httpRequest.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
            }

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
            await EnqueueDiagnosticAsync(
                sessionId: sessionId,
                component: RuntimeDiagnosticComponent.Model,
                phase: RuntimeDiagnosticPhase.Request,
                eventType: "model.request_failed",
                severity: RuntimeDiagnosticSeverity.Error,
                errorCode: RuntimeDiagnosticErrorCodes.ModelRequestFailed,
                message: ex.Message).ConfigureAwait(false);
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
                await EnqueueDiagnosticAsync(
                    sessionId: sessionId,
                    component: RuntimeDiagnosticComponent.Storage,
                    phase: RuntimeDiagnosticPhase.Persist,
                    eventType: "storage.persist_failed",
                    severity: RuntimeDiagnosticSeverity.Warning,
                    errorCode: RuntimeDiagnosticErrorCodes.StoragePersistFailed,
                    message: $"session_http_log failed: {logEx.Message}").ConfigureAwait(false);
            }
        }
    }

    private async Task EnqueueDiagnosticAsync(
        string? sessionId,
        RuntimeDiagnosticComponent component,
        RuntimeDiagnosticPhase phase,
        string eventType,
        RuntimeDiagnosticSeverity severity,
        string errorCode,
        string? message)
    {
        if (_runtimeDiagnosticEventSink is not { } sink)
        {
            return;
        }

        var runId = _runContextAccessor?.Current?.RunId ?? sessionId;
        var evt = new RuntimeDiagnosticEvent(
            eventId: "",
            ts: default,
            sequence: 0,
            sessionId: sessionId,
            runId: runId,
            turnId: null,
            attemptId: null,
            parentAttemptId: null,
            toolCallId: null,
            messageId: null,
            component: component,
            phase: phase,
            eventType: eventType,
            severity: severity,
            errorCode: errorCode,
            message: message);
        await sink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);
    }
}
