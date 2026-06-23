using System.Net;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core.Sso;

namespace Athlon.Agent.Infrastructure.Sso;

public sealed class ImpSsoCallbackServer : IDisposable
{
    private static readonly TimeSpan StopDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly SsoSettings _settings;
    private readonly object _inflightLock = new();
    private readonly object _pendingLock = new();
    private int _inflightRequestCount;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private TaskCompletionSource<ImpSsoCallbackPayload>? _callbackTcs;
    private HttpListenerContext? _pendingCompleteContext;
    private TaskCompletionSource? _pendingResponseTcs;
    private bool _callbackReceived;
    private bool _disposed;

    public ImpSsoCallbackServer(SsoSettings settings)
    {
        _settings = settings;
    }

    public async Task<ImpSsoCallbackPayload> WaitForCallbackAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _callbackTcs = new TaskCompletionSource<ImpSsoCallbackPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await StartAsync(cancellationToken).ConfigureAwait(false);

        using var timeoutCts = new CancellationTokenSource(timeout);
        await using var cancelRegistration = cancellationToken.Register(() =>
            _callbackTcs!.TrySetCanceled(cancellationToken));
        await using var timeoutRegistration = timeoutCts.Token.Register(() =>
            _callbackTcs!.TrySetException(new TimeoutException("IMP 登录超时，请重试。")));

        return await _callbackTcs.Task.ConfigureAwait(false);
    }

    public async Task CompleteBrowserResponseAsync(
        ImpSsoCheckResult result,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        HttpListenerContext? context;
        TaskCompletionSource? responseTcs;
        lock (_pendingLock)
        {
            context = _pendingCompleteContext;
            responseTcs = _pendingResponseTcs;
            _pendingCompleteContext = null;
            _pendingResponseTcs = null;
        }

        if (context is null || responseTcs is null)
        {
            return;
        }

        var message = result.IsValid
            ? "登录成功"
            : string.IsNullOrWhiteSpace(result.Message) ? "验证未通过。" : result.Message;
        var statusCode = result.IsValid ? HttpStatusCode.OK : HttpStatusCode.Unauthorized;
        await WriteJsonAsync(context.Response, statusCode, new { ok = result.IsValid, message })
            .ConfigureAwait(false);
        responseTcs.TrySetResult();
    }

    public Task AbortPendingBrowserResponseAsync()
    {
        HttpListenerContext? context;
        TaskCompletionSource? responseTcs;
        lock (_pendingLock)
        {
            context = _pendingCompleteContext;
            responseTcs = _pendingResponseTcs;
            _pendingCompleteContext = null;
            _pendingResponseTcs = null;
        }

        if (context is null || responseTcs is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            context.Response.Close();
        }
        catch
        {
            // ignored
        }

        responseTcs.TrySetResult();
        return Task.CompletedTask;
    }

    private Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_settings.CallbackPort}/");
        _listener.Start();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
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

            _ = HandleRequestAsync(context);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        BeginInflightRequest();
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "";
            if (string.Equals(path, _settings.CallbackPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAuthPageAsync(context.Response).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, _settings.CompletePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCompleteAsync(context).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
        }
        catch
        {
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
            }
            catch
            {
                // ignored
            }
        }
        finally
        {
            EndInflightRequest();
        }
    }

    private async Task HandleCompleteAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        ImpSsoCallbackPayload? payload = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            payload = JsonSerializer.Deserialize<ImpSsoCallbackPayload>(body, ImpSsoJson.Options);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Token))
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.BadRequest,
                new { ok = false, message = "回调缺少 token。" }).ConfigureAwait(false);
            return;
        }

        TaskCompletionSource? responseTcs = null;
        var isDuplicate = false;
        lock (_pendingLock)
        {
            if (_callbackReceived)
            {
                isDuplicate = true;
            }
            else
            {
                _callbackReceived = true;
                _pendingCompleteContext = context;
                responseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingResponseTcs = responseTcs;
            }
        }

        if (isDuplicate)
        {
            await WriteJsonAsync(
                context.Response,
                HttpStatusCode.Conflict,
                new { ok = false, message = "登录已在处理中" }).ConfigureAwait(false);
            return;
        }

        _callbackTcs?.TrySetResult(payload);
        if (responseTcs is not null)
        {
            await responseTcs.Task.ConfigureAwait(false);
        }
    }

    private async Task WriteAuthPageAsync(HttpListenerResponse response)
    {
        var html = ImpSsoAuthPageHtml.Build(_settings.CompletePath);
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode statusCode, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();

        if (_listener is { IsListening: true })
        {
            try
            {
                _listener.Stop();
            }
            catch (HttpListenerException)
            {
                // Listener already closed.
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
                // Expected when stopping.
            }
            catch (HttpListenerException)
            {
                // Expected when stopping.
            }
            catch (ObjectDisposedException)
            {
                // Expected when stopping.
            }
        }

        await WaitForInflightRequestsAsync().ConfigureAwait(false);

        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }

        _listener = null;
    }

    private void BeginInflightRequest()
    {
        lock (_inflightLock)
        {
            _inflightRequestCount++;
        }
    }

    private void EndInflightRequest()
    {
        lock (_inflightLock)
        {
            _inflightRequestCount--;
            if (_inflightRequestCount == 0)
            {
                Monitor.PulseAll(_inflightLock);
            }
        }
    }

    private async Task WaitForInflightRequestsAsync()
    {
        var deadline = DateTime.UtcNow.Add(StopDrainTimeout);
        while (DateTime.UtcNow < deadline)
        {
            lock (_inflightLock)
            {
                if (_inflightRequestCount == 0)
                {
                    return;
                }
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
        _cts = null;
    }
}
