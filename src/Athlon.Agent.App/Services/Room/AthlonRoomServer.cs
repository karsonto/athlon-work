using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Athlon.Agent.App.Services.Room;

/// <summary>
/// LAN-bound HTTP server for Athlon chat rooms (static SPA + REST + SSE).
/// </summary>
public sealed class AthlonRoomServer : IAsyncDisposable
{
    public const int DefaultPort = 6890;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ico"] = "image/x-icon",
        [".map"] = "application/json"
    };

    private readonly RoomStore _store;
    private readonly ILogger<AthlonRoomServer> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _sseConnections = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _port = DefaultPort;
    private string? _lanIp;
    private string? _activeRoomId;

    public AthlonRoomServer(RoomStore store, ILogger<AthlonRoomServer> logger)
    {
        _store = store;
        _logger = logger;
    }

    public bool IsRunning => _listener?.IsListening == true;

    public int Port => _port;

    public string? LanIp => _lanIp;

    public string? ActiveRoomId => _activeRoomId;

    public string? ActiveInviteUrl
    {
        get
        {
            if (_activeRoomId is null || _lanIp is null)
            {
                return null;
            }

            var room = _store.GetRoom(_activeRoomId);
            return room is null ? null : BuildInviteUrl(_lanIp, _port, room.Id, room.Token);
        }
    }

    public RoomInfo? ActiveRoom =>
        _activeRoomId is null ? null : _store.GetRoom(_activeRoomId);

    public async Task<RoomCreateResult> StartOrCreateRoomAsync(string? title = null, int? port = null, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _port = port is > 0 and < 65535 ? port.Value : DefaultPort;
            var room = _store.CreateRoom(title);
            _activeRoomId = room.Id;

            if (!IsRunning)
            {
                await StartListenerAsync(cancellationToken).ConfigureAwait(false);
            }

            var invite = BuildInviteUrl(_lanIp!, _port, room.Id, room.Token);
            return new RoomCreateResult
            {
                Room = room,
                InviteUrl = invite,
                LanIp = _lanIp!,
                Port = _port
            };
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeRoomId is not null)
            {
                try
                {
                    _store.InvalidateToken(_activeRoomId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to invalidate room token.");
                }
            }

            _activeRoomId = null;
            await StopListenerCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task StartListenerAsync(CancellationToken cancellationToken)
    {
        _lanIp = ResolveLanIpv4() ?? "127.0.0.1";
        var contentRoot = AthlonRoomAssets.AssetsDirectory;
        if (!Directory.Exists(contentRoot))
        {
            Directory.CreateDirectory(contentRoot);
        }

        Exception? lastError = null;
        foreach (var prefixes in BuildPrefixCandidates(_port, _lanIp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listener = new HttpListener();
            try
            {
                foreach (var prefix in prefixes)
                {
                    listener.Prefixes.Add(prefix);
                }

                listener.Start();
                _listener = listener;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => AcceptLoopAsync(listener, contentRoot, token), token);
                _logger.LogInformation("AthlonRoom listening on {Prefixes} (LAN {LanIp})", string.Join(", ", prefixes), _lanIp);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try
                {
                    listener.Close();
                }
                catch
                {
                    // ignore
                }
            }
        }

        throw new InvalidOperationException(
            $"Unable to bind AthlonRoom on port {_port}. Try another port or run with elevated permissions.",
            lastError);
    }

    private async Task StopListenerCoreAsync()
    {
        var cts = _cts;
        _cts = null;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // ignore
            }

            cts.Dispose();
        }

        var listener = _listener;
        _listener = null;
        if (listener is not null)
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // ignore
            }

            try
            {
                listener.Close();
            }
            catch
            {
                // ignore
            }
        }

        var loop = _loop;
        _loop = null;
        if (loop is not null)
        {
            try
            {
                await Task.WhenAny(loop, Task.Delay(2000)).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, string contentRoot, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
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

            _ = Task.Run(() => HandleRequestAsync(context, contentRoot), cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, string contentRoot)
    {
        try
        {
            AddCors(context.Response);
            if (string.Equals(context.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleApiAsync(context).ConfigureAwait(false);
                return;
            }

            // SPA routes: /r/{roomId}
            await ServeStaticAsync(context, contentRoot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Room request failed.");
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
            }
            catch
            {
                // best effort
            }
        }
    }

    private async Task HandleApiAsync(HttpListenerContext context)
    {
        var method = context.Request.HttpMethod ?? "GET";
        var path = context.Request.Url?.AbsolutePath ?? "";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // POST /api/rooms — local create (also used by desktop; no token required, localhost preferred)
        if (segments.Length == 2
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("rooms", StringComparison.OrdinalIgnoreCase)
            && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsLocalRequest(context.Request))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.Forbidden, new { error = "local_only" }).ConfigureAwait(false);
                return;
            }

            string? title = null;
            try
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("title", out var t))
                    {
                        title = t.GetString();
                    }
                }
            }
            catch
            {
                // ignore body parse
            }

            var result = await StartOrCreateRoomAsync(title).ConfigureAwait(false);
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
            {
                id = result.Room.Id,
                title = result.Room.Title,
                inviteUrl = result.InviteUrl,
                lanIp = result.LanIp,
                port = result.Port,
                createdAt = result.Room.CreatedAt
            }).ConfigureAwait(false);
            return;
        }

        // /api/rooms/{id}/...
        if (segments.Length < 3
            || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("rooms", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "not_found" }).ConfigureAwait(false);
            return;
        }

        var roomId = segments[2];
        if (!RoomStore.IsSafeId(roomId) || _store.GetRoom(roomId) is null)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "room_not_found" }).ConfigureAwait(false);
            return;
        }

        var token = ExtractToken(context.Request);
        if (!_store.ValidateToken(roomId, token))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.Unauthorized, new { error = "unauthorized" }).ConfigureAwait(false);
            return;
        }

        // GET /api/rooms/{id}
        if (segments.Length == 3 && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            var room = _store.GetRoom(roomId)!;
            await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
            {
                id = room.Id,
                title = room.Title,
                createdAt = room.CreatedAt
            }).ConfigureAwait(false);
            return;
        }

        if (segments.Length >= 4)
        {
            var action = segments[3];

            // GET /api/rooms/{id}/messages?after=
            if (action.Equals("messages", StringComparison.OrdinalIgnoreCase)
                && segments.Length == 4
                && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                var after = context.Request.QueryString["after"];
                var messages = _store.GetMessages(roomId, after);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new { messages }).ConfigureAwait(false);
                return;
            }

            // POST /api/rooms/{id}/messages
            if (action.Equals("messages", StringComparison.OrdinalIgnoreCase)
                && segments.Length == 4
                && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePostMessageAsync(context, roomId).ConfigureAwait(false);
                return;
            }

            // POST /api/rooms/{id}/attachments
            if (action.Equals("attachments", StringComparison.OrdinalIgnoreCase)
                && segments.Length == 4
                && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleUploadAsync(context, roomId).ConfigureAwait(false);
                return;
            }

            // GET /api/rooms/{id}/attachments/{aid}
            if (action.Equals("attachments", StringComparison.OrdinalIgnoreCase)
                && segments.Length == 5
                && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDownloadAsync(context, roomId, segments[4]).ConfigureAwait(false);
                return;
            }

            // GET /api/rooms/{id}/events (SSE)
            if (action.Equals("events", StringComparison.OrdinalIgnoreCase)
                && segments.Length == 4
                && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSseAsync(context, roomId).ConfigureAwait(false);
                return;
            }
        }

        await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "not_found" }).ConfigureAwait(false);
    }

    private async Task HandlePostMessageAsync(HttpListenerContext context, string roomId)
    {
        PostMessageRequest? req;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            req = JsonSerializer.Deserialize<PostMessageRequest>(body, JsonOptions);
        }
        catch
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "invalid_json" }).ConfigureAwait(false);
            return;
        }

        if (req is null || string.IsNullOrWhiteSpace(req.DisplayName))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "display_name_required" }).ConfigureAwait(false);
            return;
        }

        var attachments = new List<RoomAttachmentInfo>();
        if (req.AttachmentIds is { Count: > 0 })
        {
            foreach (var aid in req.AttachmentIds)
            {
                var meta = _store.GetAttachmentMeta(roomId, aid);
                if (meta is null)
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "attachment_not_found", id = aid })
                        .ConfigureAwait(false);
                    return;
                }

                attachments.Add(meta);
            }
        }

        var hasText = !string.IsNullOrWhiteSpace(req.Text);
        if (!hasText && attachments.Count == 0)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "empty_message" }).ConfigureAwait(false);
            return;
        }

        var kind = attachments.Count == 0
            ? "text"
            : attachments.All(a => a.IsImage) && !hasText
                ? "image"
                : attachments.Count > 0 && !hasText
                    ? "file"
                    : "text";

        var message = new RoomMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            SenderId = string.IsNullOrWhiteSpace(req.SenderId) ? Guid.NewGuid().ToString("N")[..8] : req.SenderId.Trim(),
            DisplayName = req.DisplayName.Trim(),
            Kind = kind,
            Text = hasText ? req.Text!.Trim() : null,
            Attachments = attachments,
            CreatedAt = DateTimeOffset.UtcNow
        };

        message = _store.AppendMessage(roomId, message);
        await WriteJsonAsync(context.Response, HttpStatusCode.OK, message).ConfigureAwait(false);
    }

    private async Task HandleUploadAsync(HttpListenerContext context, string roomId)
    {
        try
        {
            var contentType = context.Request.ContentType ?? "";
            string fileName;
            string mime;
            Stream contentStream;

            if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = await ParseMultipartAsync(context.Request).ConfigureAwait(false);
                if (parsed is null)
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "invalid_multipart" }).ConfigureAwait(false);
                    return;
                }

                fileName = parsed.Value.FileName;
                mime = parsed.Value.MimeType;
                contentStream = parsed.Value.Content;
            }
            else
            {
                fileName = context.Request.QueryString["fileName"]
                    ?? context.Request.Headers["X-File-Name"]
                    ?? "upload.bin";
                mime = context.Request.QueryString["mime"]
                    ?? context.Request.ContentType
                    ?? "application/octet-stream";
                contentStream = context.Request.InputStream;
            }

            await using (contentStream)
            {
                if (context.Request.ContentLength64 > RoomStore.MaxAttachmentBytes)
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.RequestEntityTooLarge, new { error = "too_large" })
                        .ConfigureAwait(false);
                    return;
                }

                var info = _store.SaveAttachment(roomId, fileName, mime, contentStream, context.Request.ContentLength64);
                var previewUrl = $"/api/rooms/{roomId}/attachments/{info.Id}?t={Uri.EscapeDataString(ExtractToken(context.Request) ?? "")}";
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new
                {
                    id = info.Id,
                    fileName = info.FileName,
                    mimeType = info.MimeType,
                    size = info.Size,
                    isImage = info.IsImage,
                    previewUrl
                }).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.RequestEntityTooLarge, new { error = "too_large" }).ConfigureAwait(false);
        }
    }

    private async Task HandleDownloadAsync(HttpListenerContext context, string roomId, string attachmentId)
    {
        if (!RoomStore.IsSafeId(attachmentId))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new { error = "invalid_id" }).ConfigureAwait(false);
            return;
        }

        var meta = _store.GetAttachmentMeta(roomId, attachmentId);
        var path = _store.GetAttachmentFilePath(roomId, attachmentId);
        if (meta is null || path is null)
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new { error = "not_found" }).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = meta.MimeType;
        var disposition = meta.IsImage ? "inline" : "attachment";
        context.Response.Headers["Content-Disposition"] =
            $"{disposition}; filename=\"{meta.FileName.Replace("\"", "", StringComparison.Ordinal)}\"";

        await using var stream = File.OpenRead(path);
        context.Response.ContentLength64 = stream.Length;
        await stream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
        context.Response.Close();
    }

    private async Task HandleSseAsync(HttpListenerContext context, string roomId)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        _sseConnections[connectionId] = 0;

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";
        context.Response.SendChunked = true;

        var output = context.Response.OutputStream;
        await WriteSseAsync(output, "ready", new { ok = true }).ConfigureAwait(false);

        using var subscription = _store.Subscribe(roomId, message =>
        {
            try
            {
                var json = JsonSerializer.Serialize(message, JsonOptions);
                var payload = $"event: message\ndata: {json}\n\n";
                var bytes = Encoding.UTF8.GetBytes(payload);
                lock (output)
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush();
                }
            }
            catch
            {
                // connection likely closed
            }
        });

        try
        {
            // Keep alive until client disconnects or server stops
            var buffer = new byte[1];
            while (_listener?.IsListening == true)
            {
                await Task.Delay(15000).ConfigureAwait(false);
                try
                {
                    var ping = Encoding.UTF8.GetBytes(": ping\n\n");
                    lock (output)
                    {
                        output.Write(ping, 0, ping.Length);
                        output.Flush();
                    }
                }
                catch
                {
                    break;
                }

                // Detect closed connection via reading (non-blocking style unavailable); ping failure exits.
                _ = buffer;
            }
        }
        catch
        {
            // client gone
        }
        finally
        {
            _sseConnections.TryRemove(connectionId, out _);
            try
            {
                context.Response.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task ServeStaticAsync(HttpListenerContext context, string contentRoot)
    {
        var relative = ResolveStaticRelativePath(context.Request.Url);
        if (relative is null)
        {
            await WriteTextAsync(context.Response, HttpStatusCode.Forbidden, "Forbidden").ConfigureAwait(false);
            return;
        }

        var filePath = Path.Combine(contentRoot, relative);
        if (Directory.Exists(filePath))
        {
            filePath = Path.Combine(filePath, AthlonRoomAssets.EntryFileName);
        }

        // SPA fallback for /r/{roomId} only — never for real static files (.js/.css/...).
        if (!File.Exists(filePath))
        {
            var abs = context.Request.Url?.AbsolutePath ?? "";
            var ext = Path.GetExtension(abs);
            if (string.IsNullOrEmpty(ext)
                && (abs.StartsWith("/r/", StringComparison.OrdinalIgnoreCase)
                    || abs.Equals("/r", StringComparison.OrdinalIgnoreCase)
                    || abs == "/"))
            {
                filePath = Path.Combine(contentRoot, AthlonRoomAssets.EntryFileName);
            }
        }

        if (!File.Exists(filePath) || !IsPathWithinRoot(contentRoot, filePath))
        {
            await WriteTextAsync(context.Response, HttpStatusCode.NotFound, "Not Found").ConfigureAwait(false);
            return;
        }

        var mime = ResolveMimeType(filePath);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = mime;
        await using var stream = File.OpenRead(filePath);
        context.Response.ContentLength64 = stream.Length;
        await stream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task<(string FileName, string MimeType, Stream Content)?> ParseMultipartAsync(HttpListenerRequest request)
    {
        var contentType = request.ContentType ?? "";
        var match = Regex.Match(contentType, @"boundary=(?:"")?([^"";]+)(?:"")?", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var boundary = match.Groups[1].Value;
        using var ms = new MemoryStream();
        await request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
        var bytes = ms.ToArray();
        if (bytes.Length > RoomStore.MaxAttachmentBytes + 4096)
        {
            throw new InvalidOperationException($"Attachment exceeds {RoomStore.MaxAttachmentBytes} bytes.");
        }

        var text = Encoding.UTF8.GetString(bytes);
        var delimiter = "--" + boundary;
        var parts = text.Split(new[] { delimiter }, StringSplitOptions.None);
        foreach (var part in parts)
        {
            if (part.StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            var trimmed = part.TrimStart('\r', '\n');
            var headerEnd = trimmed.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                continue;
            }

            var headers = trimmed[..headerEnd];
            if (!headers.Contains("filename=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nameMatch = Regex.Match(headers, @"filename=""([^""]*)""", RegexOptions.IgnoreCase);
            var fileName = nameMatch.Success ? nameMatch.Groups[1].Value : "upload.bin";
            var mimeMatch = Regex.Match(headers, @"Content-Type:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            var mime = mimeMatch.Success ? mimeMatch.Groups[1].Value.Trim() : "application/octet-stream";

            // Re-extract binary from original bytes to avoid UTF-8 corruption for images
            var headerBytes = Encoding.UTF8.GetBytes(trimmed[..(headerEnd + 4)]);
            var partStartInBody = IndexOf(bytes, Encoding.UTF8.GetBytes(trimmed[..Math.Min(64, trimmed.Length)]));
            // Fallback: locate filename section then \r\n\r\n
            var fileHeaderMarker = Encoding.UTF8.GetBytes("filename=");
            var idx = IndexOf(bytes, fileHeaderMarker);
            if (idx < 0)
            {
                var contentStart = headerEnd + 4;
                var content = trimmed[contentStart..];
                if (content.EndsWith("\r\n", StringComparison.Ordinal))
                {
                    content = content[..^2];
                }

                return (fileName, mime, new MemoryStream(Encoding.UTF8.GetBytes(content)));
            }

            var headerSep = Encoding.UTF8.GetBytes("\r\n\r\n");
            var contentBegin = IndexOf(bytes, headerSep, idx);
            if (contentBegin < 0)
            {
                continue;
            }

            contentBegin += headerSep.Length;
            var nextBoundary = Encoding.UTF8.GetBytes("\r\n--" + boundary);
            var contentEnd = IndexOf(bytes, nextBoundary, contentBegin);
            if (contentEnd < 0)
            {
                contentEnd = bytes.Length;
            }

            var length = contentEnd - contentBegin;
            if (length < 0)
            {
                continue;
            }

            var buffer = new byte[length];
            Buffer.BlockCopy(bytes, contentBegin, buffer, 0, length);
            _ = headerBytes;
            _ = partStartInBody;
            return (fileName, mime, new MemoryStream(buffer));
        }

        return null;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }

    private static string? ExtractToken(HttpListenerRequest request)
    {
        var auth = request.Headers["Authorization"];
        if (!string.IsNullOrWhiteSpace(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth["Bearer ".Length..].Trim();
        }

        return request.QueryString["t"];
    }

    private static bool IsLocalRequest(HttpListenerRequest request)
    {
        var addr = request.RemoteEndPoint?.Address;
        if (addr is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(addr))
        {
            return true;
        }

        // IPv4-mapped
        if (addr.IsIPv4MappedToIPv6)
        {
            return IPAddress.IsLoopback(addr.MapToIPv4());
        }

        return false;
    }

    private static IEnumerable<string[]> BuildPrefixCandidates(int port, string lanIp)
    {
        // Prefer explicit LAN + loopback (no admin on Windows for +)
        yield return new[]
        {
            $"http://{lanIp}:{port}/",
            $"http://127.0.0.1:{port}/"
        };

        yield return new[] { $"http://127.0.0.1:{port}/" };

        yield return new[] { $"http://+:{port}/" };
    }

    private static string? ResolveLanIpv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var props = ni.GetIPProperties();
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (IPAddress.IsLoopback(uni.Address))
                    {
                        continue;
                    }

                    var bytes = uni.Address.GetAddressBytes();
                    // skip APIPA
                    if (bytes[0] == 169 && bytes[1] == 254)
                    {
                        continue;
                    }

                    return uni.Address.ToString();
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static string BuildInviteUrl(string lanIp, int port, string roomId, string token) =>
        $"http://{lanIp}:{port}/r/{roomId}?t={Uri.EscapeDataString(token)}";

    private static string? ResolveStaticRelativePath(Uri? requestUri)
    {
        if (requestUri is null)
        {
            return AthlonRoomAssets.EntryFileName;
        }

        var rawPath = Uri.UnescapeDataString(requestUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath == "/")
        {
            return AthlonRoomAssets.EntryFileName;
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

        // SPA client routes: /r or /r/{roomId} only (not /r/assets/...).
        if (segments.Length == 1
            && segments[0].Equals("r", StringComparison.OrdinalIgnoreCase))
        {
            return AthlonRoomAssets.EntryFileName;
        }

        if (segments.Length == 2
            && segments[0].Equals("r", StringComparison.OrdinalIgnoreCase)
            && RoomStore.IsSafeId(segments[1]))
        {
            return AthlonRoomAssets.EntryFileName;
        }

        return Path.Combine(segments);
    }

    private static bool IsPathWithinRoot(string rootDirectory, string candidatePath)
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

    private static void AddCors(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, X-File-Name";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode status, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        await WriteTextAsync(response, status, json, "application/json; charset=utf-8").ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(
        HttpListenerResponse response,
        HttpStatusCode status,
        string body,
        string contentType = "text/plain; charset=utf-8")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = (int)status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static async Task WriteSseAsync(Stream output, string eventName, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        var payload = $"event: {eventName}\ndata: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await output.WriteAsync(bytes).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }
}
