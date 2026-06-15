using System.Net;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core.Sso;

namespace Athlon.Agent.Infrastructure.Sso;

public sealed class ImpSsoCallbackServer : IDisposable
{
    private readonly SsoSettings _settings;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private TaskCompletionSource<ImpSsoCallbackPayload>? _callbackTcs;

    public ImpSsoCallbackServer(SsoSettings settings)
    {
        _settings = settings;
    }

    public async Task<ImpSsoCallbackPayload> WaitForCallbackAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        _callbackTcs = new TaskCompletionSource<ImpSsoCallbackPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await StartAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await using var registration = timeoutCts.Token.Register(() =>
            _callbackTcs.TrySetException(new TimeoutException("IMP 登录超时，请重试。")));

        try
        {
            return await _callbackTcs.Task;
        }
        finally
        {
            await StopAsync();
        }
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
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
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

            _ = Task.Run(() => HandleRequestAsync(context), cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "";
            if (string.Equals(path, _settings.CallbackPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAuthPageAsync(context.Response);
                return;
            }

            if (string.Equals(path, _settings.CompletePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCompleteAsync(context);
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
    }

    private async Task HandleCompleteAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        ImpSsoCallbackPayload? payload = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            payload = JsonSerializer.Deserialize<ImpSsoCallbackPayload>(body, ImpSsoJson.Options);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Token))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { ok = false });
            return;
        }

        _callbackTcs?.TrySetResult(payload);
        await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { ok = true });
    }

    private async Task WriteAuthPageAsync(HttpListenerResponse response)
    {
        var completePath = _settings.CompletePath;
        var html = $$"""
            <!DOCTYPE html>
            <html>
            <head><meta charset="utf-8"><title>SSO 登录中</title></head>
            <body>
            <p>正在完成登录，请稍候...</p>
            <script>
            (function () {
              var hash = window.location.hash || '';
              if (hash.indexOf('#/?') === 0) hash = hash.substring(3);
              else if (hash.indexOf('#/') === 0) hash = hash.substring(2);
              else if (hash.charAt(0) === '#') hash = hash.substring(1);
              var params = new URLSearchParams(hash);
              var payload = {
                appId: params.get('appId'),
                userId: params.get('userId'),
                locale: params.get('locale'),
                token: params.get('token'),
                role: params.get('role'),
                depname: params.get('depname'),
                channel_type: params.get('channel_type'),
                msg: params.get('msg')
              };
              if (!payload.token) {
                document.body.innerHTML = '<p>登录失败：未收到 token。</p>';
                return;
              }
              fetch('{{completePath}}', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
              })
              .then(function (r) { return r.json(); })
              .then(function () {
                document.body.innerHTML = '<p>登录成功，可以关闭此窗口。</p>';
              })
              .catch(function () {
                document.body.innerHTML = '<p>登录失败，请返回应用重试。</p>';
              });
            })();
            </script>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode statusCode, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        if (_listener is { IsListening: true })
        {
            _listener.Stop();
        }

        return _listenTask ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _listener?.Close();
        _listener = null;
    }
}
