using System.Collections.Frozen;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

public sealed class AthlonWebStaticServer : IAsyncDisposable
{
    /// <summary>Loopback port for the bundled Athlon Web static site.</summary>
    public const int FixedPort = 6888;
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
    private readonly int _port;
    private readonly AthlonWebChatForwarder? _forwarder;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int _started;

    public AthlonWebStaticServer(AthlonWebChatForwarder forwarder)
        : this(AthlonWebAssets.AssetsDirectory, FixedPort, forwarder)
    {
    }

    internal AthlonWebStaticServer(string contentRoot, int port = FixedPort, AthlonWebChatForwarder? forwarder = null)
    {
        _contentRoot = Path.GetFullPath(contentRoot);
        _port = port;
        _forwarder = forwarder;
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

            var bound = BindListener(_port);
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

            var listener = _listener;
            _listener = null;
            BaseUrl = null;

            if (listener is not null)
            {
                try
                {
                    if (listener.IsListening)
                    {
                        listener.Stop();
                    }

                    listener.Close();
                    listener.Abort();
                }
                catch (HttpListenerException)
                {
                    // Listener already closed.
                }
                catch (ObjectDisposedException)
                {
                    // Listener already torn down.
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
            _cts?.Dispose();
            _cts = null;
            _listenTask = null;
            Interlocked.Exchange(ref _started, 0);
        }
        finally
        {
            _startLock.Release();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private static (HttpListener Listener, string Url) BindListener(int port)
    {
        var bindPort = port > 0 ? port : GetEphemeralPort();
        HttpListenerException? last = null;
        foreach (var host in new[] { "127.0.0.1", "localhost" })
        {
            var url = $"http://{host}:{bindPort}/";
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

        throw new InvalidOperationException(
            $"Unable to bind Athlon Web listener on loopback port {bindPort}.",
            last);
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
            if (_forwarder is not null
                && AthlonWebChatForwarder.IsChatCompletionRequest(context.Request.HttpMethod, context.Request.Url))
            {
                await ForwardChatAsync(context).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(context.Response, HttpStatusCode.MethodNotAllowed, "text/plain; charset=utf-8", "Method Not Allowed")
                    .ConfigureAwait(false);
                return;
            }

            var relativePath = ResolveRelativePath(context.Request);
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

    internal static string? ResolveRelativePath(HttpListenerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // RawUrl is the request target. On Linux, HttpListener may also expose a Uri whose
        // AbsolutePath has already collapsed ".." while OriginalString still contains it.
        // Reject when either form escapes the root.
        var rawTarget = request.RawUrl;
        if (!string.IsNullOrEmpty(rawTarget))
        {
            var queryIndex = rawTarget.IndexOf('?', StringComparison.Ordinal);
            if (queryIndex >= 0)
            {
                rawTarget = rawTarget[..queryIndex];
            }
        }

        var originalTarget = request.Url is null ? null : GetRawRequestPath(request.Url);
        if (ResolveRawPath(rawTarget ?? string.Empty) is null
            || (originalTarget is not null && ResolveRawPath(originalTarget) is null))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(rawTarget))
        {
            return ResolveRawPath(rawTarget);
        }

        return ResolveRelativePath(request.Url);
    }

    internal static string? ResolveRelativePath(Uri? requestUri)
    {
        if (requestUri is null)
        {
            return AthlonWebAssets.EntryFileName;
        }

        return ResolveRawPath(GetRawRequestPath(requestUri));
    }

    internal static string? ResolveRawPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath == "/")
        {
            return AthlonWebAssets.EntryFileName;
        }

        var trimmed = rawPath.TrimStart('/');
        var segments = new List<string>();
        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            if (segment.Contains("..", StringComparison.Ordinal)
                || segment.Contains(Path.DirectorySeparatorChar)
                || segment.Contains(Path.AltDirectorySeparatorChar))
            {
                return null;
            }

            segments.Add(segment);
        }

        return segments.Count == 0
            ? AthlonWebAssets.EntryFileName
            : Path.Combine(segments.ToArray());
    }

    /// <summary>
    /// Reads the path component before <see cref="Uri"/> collapses <c>..</c> segments.
    /// </summary>
    internal static string GetRawRequestPath(Uri requestUri)
    {
        var original = requestUri.OriginalString;
        var schemeIndex = original.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex < 0)
        {
            return Uri.UnescapeDataString(requestUri.AbsolutePath);
        }

        var pathStart = original.IndexOf('/', schemeIndex + 3);
        if (pathStart < 0)
        {
            return "/";
        }

        var pathEnd = original.Length;
        for (var index = pathStart; index < original.Length; index++)
        {
            if (original[index] is '?' or '#')
            {
                pathEnd = index;
                break;
            }
        }

        return Uri.UnescapeDataString(original[pathStart..pathEnd]);
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

    private async Task ForwardChatAsync(HttpListenerContext context)
    {
        try
        {
            using var upstream = await _forwarder!.SendAsync(context.Request.InputStream).ConfigureAwait(false);
            await WriteUpstreamAsync(context.Response, upstream).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                await WriteResponseAsync(context.Response, HttpStatusCode.BadGateway, "text/plain; charset=utf-8", "Bad Gateway")
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static async Task WriteUpstreamAsync(HttpListenerResponse response, HttpResponseMessage upstream)
    {
        response.StatusCode = (int)upstream.StatusCode;
        var mediaType = upstream.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            response.ContentType = mediaType;
        }

        response.SendChunked = true;
        await using var stream = await upstream.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await response.OutputStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            await response.OutputStream.FlushAsync().ConfigureAwait(false);
        }

        response.Close();
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
