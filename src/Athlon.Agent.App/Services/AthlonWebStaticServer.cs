using System.Collections.Frozen;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Athlon.Agent.App.Services;

public sealed class AthlonWebStaticServer : IAsyncDisposable
{
    private static readonly FrozenDictionary<string, string> MimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".webp"] = "image/webp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".map"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".wasm"] = "application/wasm",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly string _contentRoot;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int _started;

    public AthlonWebStaticServer()
        : this(AthlonWebAssets.AssetsDirectory)
    {
    }

    internal AthlonWebStaticServer(string contentRoot)
    {
        _contentRoot = Path.GetFullPath(contentRoot);
    }

    public string? BaseUrl { get; private set; }

    public async Task<string> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (BaseUrl is not null)
        {
            return BaseUrl;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (BaseUrl is not null)
            {
                return BaseUrl;
            }

            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                return BaseUrl ?? throw new InvalidOperationException("Athlon Web server failed to start.");
            }

            if (!Directory.Exists(_contentRoot))
            {
                throw new DirectoryNotFoundException($"Athlon Web assets directory not found: {_contentRoot}");
            }

            var bound = BindListener();
            _listener = bound.Listener;
            BaseUrl = bound.Url;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);
            return BaseUrl;
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cts is not null)
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }

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
                    // Expected on shutdown.
                }
            }

            _listener?.Close();
            _listener = null;
            _cts?.Dispose();
            _cts = null;
            _listenTask = null;
            BaseUrl = null;
            Interlocked.Exchange(ref _started, 0);
        }
        finally
        {
            _startLock.Release();
        }
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

        throw new InvalidOperationException("Unable to bind Athlon Web listener on loopback.", last);
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
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(context.Response, HttpStatusCode.MethodNotAllowed, "text/plain; charset=utf-8", "Method Not Allowed")
                    .ConfigureAwait(false);
                return;
            }

            var relativePath = ResolveRelativePath(context.Request.Url);
            if (relativePath is null)
            {
                await WriteResponseAsync(context.Response, HttpStatusCode.Forbidden, "text/plain; charset=utf-8", "Forbidden")
                    .ConfigureAwait(false);
                return;
            }

            var filePath = Path.Combine(_contentRoot, relativePath);
            if (Directory.Exists(filePath))
            {
                filePath = Path.Combine(filePath, AthlonWebAssets.EntryFileName);
            }

            if (!File.Exists(filePath))
            {
                await WriteResponseAsync(context.Response, HttpStatusCode.NotFound, "text/plain; charset=utf-8", "Not Found")
                    .ConfigureAwait(false);
                return;
            }

            if (!IsPathWithinRoot(_contentRoot, filePath))
            {
                await WriteResponseAsync(context.Response, HttpStatusCode.Forbidden, "text/plain; charset=utf-8", "Forbidden")
                    .ConfigureAwait(false);
                return;
            }

            var mimeType = ResolveMimeType(filePath);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = mimeType;

            if (string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.ContentLength64 = new FileInfo(filePath).Length;
                context.Response.Close();
                return;
            }

            await using var stream = File.OpenRead(filePath);
            context.Response.ContentLength64 = stream.Length;
            await stream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception)
        {
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
            }
            catch
            {
                // Best effort.
            }
        }
    }

    internal static string? ResolveRelativePath(Uri? requestUri)
    {
        if (requestUri is null)
        {
            return AthlonWebAssets.EntryFileName;
        }

        var rawPath = Uri.UnescapeDataString(requestUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath == "/")
        {
            return AthlonWebAssets.EntryFileName;
        }

        var trimmed = rawPath.TrimStart('/');
        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            return null;
        }

        return Path.Combine(segments);
    }

    internal static bool IsPathWithinRoot(string rootDirectory, string candidatePath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveMimeType(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return MimeTypes.TryGetValue(extension, out var mimeType)
            ? mimeType
            : "application/octet-stream";
    }

    private static async Task WriteResponseAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        string contentType,
        string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = (int)statusCode;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }
}
