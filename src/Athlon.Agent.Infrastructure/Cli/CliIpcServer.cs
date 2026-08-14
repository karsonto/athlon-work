using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Cli;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Infrastructure.Cli;

public sealed class CliIpcServer(
    IAgentOrchestrator orchestrator,
    IFileStorageService storage,
    CliSessionMap sessionMap,
    IAppPathProvider paths,
    IDesktopSessionRunProbe desktopRunProbe,
    IAppLogger logger) : IAsyncDisposable
{
    private readonly IAppLogger _logger = logger.ForContext("CliIpc");
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _turnCancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolApprovalDecision>> _pendingApprovals =
        new(StringComparer.Ordinal);
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int _started;

    public string? Url { get; private set; }

    public string? Token { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var bound = BindListener();
            _listener = bound.Listener;
            Token = token;
            Url = bound.Url;

            CliEndpointFile.Write(
                paths.RootPath,
                new CliEndpointInfo
                {
                    Url = Url,
                    Token = token,
                    Pid = Environment.ProcessId
                });

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);
            _logger.Information("CLI IPC listening on {Url}", Url);
            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0 && _listener is null)
        {
            CliEndpointFile.Delete(paths.RootPath);
            return;
        }

        foreach (var pair in _turnCancellations)
        {
            try
            {
                pair.Value.Cancel();
            }
            catch
            {
                // ignored
            }
        }

        foreach (var pair in _pendingApprovals)
        {
            pair.Value.TrySetCanceled();
        }

        _cts?.Cancel();

        if (_listener is { IsListening: true })
        {
            try
            {
                _listener.Stop();
            }
            catch (HttpListenerException)
            {
                // already stopped
            }
            catch (ObjectDisposedException)
            {
                // already disposed
            }
        }

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            catch (HttpListenerException)
            {
                // expected
            }
            catch (ObjectDisposedException)
            {
                // expected
            }
        }

        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
            // already closed
        }

        _listener = null;
        CliEndpointFile.Delete(paths.RootPath);
        _logger.Information("CLI IPC stopped");
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private static (HttpListener Listener, string Url) BindListener()
    {
        HttpListenerException? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var port = GetEphemeralPort();
            foreach (var host in new[] { "127.0.0.1", "localhost" })
            {
                var url = $"http://{host}:{port}/";
                var candidate = new HttpListener();
                candidate.Prefixes.Add(url);
                try
                {
                    candidate.Start();
                    return (candidate, url);
                }
                catch (HttpListenerException ex)
                {
                    last = ex;
                    candidate.Close();
                }
            }
        }

        throw new InvalidOperationException("Unable to bind CLI IPC listener on loopback.", last);
    }

    private static int GetEphemeralPort()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = HandleRequestAsync(context, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken shutdownToken)
    {
        try
        {
            if (!IsAuthorized(context.Request))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Unauthorized, new CliErrorResponse("unauthorized"))
                    .ConfigureAwait(false);
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "";
            var method = context.Request.HttpMethod ?? "";

            if (string.Equals(path, "/v1/health", StringComparison.OrdinalIgnoreCase)
                && HttpMethodsEqual(method, "GET"))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true, pid = Environment.ProcessId })
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/v1/turns", StringComparison.OrdinalIgnoreCase)
                && HttpMethodsEqual(method, "POST"))
            {
                await HandleTurnAsync(context, shutdownToken).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/v1/approvals", StringComparison.OrdinalIgnoreCase)
                && HttpMethodsEqual(method, "POST"))
            {
                await HandleApprovalAsync(context).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/v1/turns/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase)
                && HttpMethodsEqual(method, "POST"))
            {
                var sessionId = path["/v1/turns/".Length..];
                sessionId = sessionId[..^"/cancel".Length];
                await HandleCancelAsync(context, sessionId).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new CliErrorResponse("not found"))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "CLI IPC request failed");
            try
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.InternalServerError, new CliErrorResponse("internal error"))
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(header) || Token is null)
        {
            return false;
        }

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provided = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(Token);
        return provided.Length == expected.Length
               && CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private async Task HandleTurnAsync(HttpListenerContext context, CancellationToken shutdownToken)
    {
        CliTurnRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CliTurnRequest>(
                    context.Request.InputStream,
                    JsonFileStoreOptions.Web,
                    shutdownToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new CliErrorResponse("invalid json"))
                .ConfigureAwait(false);
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Cwd) || string.IsNullOrWhiteSpace(request.Input))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new CliErrorResponse("cwd and input are required"))
                .ConfigureAwait(false);
            return;
        }

        AgentSession session;
        try
        {
            session = await ResolveSessionAsync(request, shutdownToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.Conflict, new CliErrorResponse(ex.Message))
                .ConfigureAwait(false);
            return;
        }

        if (desktopRunProbe.IsSessionRunning(session.Id))
        {
            await WriteJsonAsync(
                    context.Response,
                    HttpStatusCode.Conflict,
                    new CliErrorResponse("当前对话正在桌面端生成，请等待完成或先停止。"))
                .ConfigureAwait(false);
            return;
        }

        var turnCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        if (!_turnCancellations.TryAdd(session.Id, turnCts))
        {
            turnCts.Dispose();
            await WriteJsonAsync(context.Response, HttpStatusCode.Conflict, new CliErrorResponse("当前对话正在生成，请等待完成或先停止。"))
                .ConfigureAwait(false);
            return;
        }

        var response = context.Response;
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.SendChunked = true;
        response.Headers["Cache-Control"] = "no-cache";

        await using var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        var writeLock = new SemaphoreSlim(1, 1);

        async Task WriteFrameAsync(string eventName, object payload)
        {
            await writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await writer.WriteAsync(CliStreamEventMapper.Format(eventName, payload)).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        await WriteFrameAsync(CliSseEventNames.Session, new CliDonePayload(session.Id)).ConfigureAwait(false);

        var callbacks = new AgentTurnCallbacks
        {
            OnStreamEvent = async streamEvent =>
            {
                var frame = CliStreamEventMapper.TryMap(streamEvent);
                if (frame is null)
                {
                    return;
                }

                await WriteFrameAsync(frame.Event, frame.Payload).ConfigureAwait(false);
            },
            OnToolApprovalRequested = async (approval, cancellationToken) =>
            {
                var tcs = new TaskCompletionSource<ToolApprovalDecision>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingApprovals[approval.ToolCallId] = tcs;
                try
                {
                    await WriteFrameAsync(
                            CliSseEventNames.ApprovalRequired,
                            new CliApprovalRequiredPayload(approval.ToolCallId, approval.ToolName))
                        .ConfigureAwait(false);
                    await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                    return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _pendingApprovals.TryRemove(approval.ToolCallId, out _);
                }
            }
        };

        try
        {
            session = await orchestrator.SendAsync(
                    session,
                    request.Input,
                    imageAttachments: null,
                    callbacks,
                    turnCts.Token)
                .ConfigureAwait(false);
            await WriteFrameAsync(CliSseEventNames.Done, new CliDonePayload(session.Id)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteFrameAsync(CliSseEventNames.Error, new CliErrorPayload("cancelled")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "CLI turn failed for session {SessionId}", session.Id);
            await WriteFrameAsync(CliSseEventNames.Error, new CliErrorPayload(ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            _turnCancellations.TryRemove(session.Id, out _);
            turnCts.Dispose();
            try
            {
                response.Close();
            }
            catch
            {
                // ignored
            }
        }
    }

    private async Task HandleApprovalAsync(HttpListenerContext context)
    {
        CliApprovalRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CliApprovalRequest>(
                    context.Request.InputStream,
                    JsonFileStoreOptions.Web)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new CliErrorResponse("invalid json"))
                .ConfigureAwait(false);
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ToolCallId))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new CliErrorResponse("toolCallId is required"))
                .ConfigureAwait(false);
            return;
        }

        if (!_pendingApprovals.TryGetValue(request.ToolCallId, out var tcs))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new CliErrorResponse("no pending approval"))
                .ConfigureAwait(false);
            return;
        }

        var decision = ParseApprovalDecision(request.Decision);
        tcs.TrySetResult(decision);
        await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true }).ConfigureAwait(false);
    }

    private async Task HandleCancelAsync(HttpListenerContext context, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new CliErrorResponse("sessionId is required"))
                .ConfigureAwait(false);
            return;
        }

        if (_turnCancellations.TryGetValue(sessionId, out var cts))
        {
            cts.Cancel();
        }

        await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true }).ConfigureAwait(false);
    }

    private async Task<AgentSession> ResolveSessionAsync(CliTurnRequest request, CancellationToken cancellationToken)
    {
        var cwd = CliPaths.NormalizeLocalPath(request.Cwd);
        string? sessionId = null;
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            sessionId = request.SessionId.Trim();
        }
        else if (!request.NewSession)
        {
            sessionId = sessionMap.Get(cwd);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var existing = await storage.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                sessionMap.Set(cwd, existing.Id);
                if (!string.Equals(existing.ActiveWorkspace, cwd, StringComparison.OrdinalIgnoreCase))
                {
                    existing = existing.WithWorkspace(cwd);
                    await storage.SaveSessionAsync(existing, cancellationToken).ConfigureAwait(false);
                }

                return existing;
            }
        }

        var folder = Path.GetFileName(cwd);
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = cwd;
        }

        var created = AgentSession.Create($"CLI · {folder}").WithWorkspace(cwd);
        await storage.SaveSessionAsync(created, cancellationToken).ConfigureAwait(false);
        sessionMap.Set(cwd, created.Id);
        return created;
    }

    private static ToolApprovalDecision ParseApprovalDecision(string? decision)
    {
        if (string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "approve", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return ToolApprovalDecision.Approved;
        }

        return ToolApprovalDecision.Denied;
    }

    private static bool HttpMethodsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode statusCode, object payload)
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType(), JsonFileStoreOptions.WebCompactRelaxed);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }
}
