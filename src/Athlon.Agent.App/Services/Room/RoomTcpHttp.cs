using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Athlon.Agent.App.Services.Room;

/// <summary>Minimal HTTP/1.1 request parsed from a <see cref="TcpClient"/> stream (no HttpListener / URL ACL).</summary>
internal sealed class RoomHttpRequest
{
    public required string Method { get; init; }

    public required string RawTarget { get; init; }

    public required string Path { get; init; }

    public required NameValueCollection QueryString { get; init; }

    public required NameValueCollection Headers { get; init; }

    public required Stream InputStream { get; init; }

    public required IPEndPoint? RemoteEndPoint { get; init; }

    public required Uri Url { get; init; }

    public string? ContentType => Headers["Content-Type"];

    public long ContentLength64
    {
        get
        {
            var raw = Headers["Content-Length"];
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
        }
    }

    public string? GetHeader(string name) => Headers[name];
}

/// <summary>Minimal HTTP/1.1 response writer over a network stream.</summary>
internal sealed class RoomHttpResponse
{
    private readonly Stream _stream;
    private readonly object _writeGate = new();
    private bool _headersSent;
    private bool _closed;

    public RoomHttpResponse(Stream stream)
    {
        _stream = stream;
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public int StatusCode { get; set; } = 200;

    public string? ContentType { get; set; }

    public long? ContentLength64 { get; set; }

    public bool KeepAlive { get; set; }

    /// <summary>When true, omit Content-Length and stream until close (SSE).</summary>
    public bool Streaming { get; set; }

    public IDictionary<string, string> Headers { get; }

    public Stream OutputStream => _stream;

    public void EnsureHeaders()
    {
        if (_headersSent || _closed)
        {
            return;
        }

        lock (_writeGate)
        {
            if (_headersSent || _closed)
            {
                return;
            }

            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ")
                .Append(StatusCode)
                .Append(' ')
                .Append(ReasonPhrase(StatusCode))
                .Append("\r\n");

            if (!string.IsNullOrWhiteSpace(ContentType))
            {
                sb.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            }

            if (!Streaming && ContentLength64 is long len)
            {
                sb.Append("Content-Length: ").Append(len.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }

            sb.Append("Connection: ").Append(KeepAlive || Streaming ? "keep-alive" : "close").Append("\r\n");

            foreach (var (key, value) in Headers)
            {
                if (string.IsNullOrWhiteSpace(key) || value is null)
                {
                    continue;
                }

                if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sb.Append(key).Append(": ").Append(value).Append("\r\n");
            }

            sb.Append("\r\n");
            var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            _stream.Write(headerBytes, 0, headerBytes.Length);
            _stream.Flush();
            _headersSent = true;
        }
    }

    public async Task WriteBodyAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        EnsureHeaders();
        await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        EnsureHeaders();
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void WriteBodySync(byte[] data)
    {
        EnsureHeaders();
        lock (_writeGate)
        {
            _stream.Write(data, 0, data.Length);
            _stream.Flush();
        }
    }

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        try
        {
            if (!_headersSent)
            {
                EnsureHeaders();
            }

            _stream.Flush();
        }
        catch
        {
            // ignore
        }

        _closed = true;
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        413 => "Payload Too Large",
        500 => "Internal Server Error",
        _ => "OK"
    };
}

internal sealed class RoomHttpContext
{
    public required RoomHttpRequest Request { get; init; }

    public required RoomHttpResponse Response { get; init; }

    public required TcpClient Client { get; init; }
}

internal static class RoomTcpHttp
{
    public const int MaxHeaderBytes = 64 * 1024;
    public const long MaxBodyBytes = RoomStore.MaxAttachmentBytes + (64 * 1024);

    public static async Task<RoomHttpContext?> AcceptRequestAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        stream.ReadTimeout = 30_000;
        stream.WriteTimeout = 30_000;

        var headerBuffer = new MemoryStream();
        var scratch = new byte[4096];
        var headerEnd = -1;

        while (headerBuffer.Length < MaxHeaderBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(scratch.AsMemory(0, scratch.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            headerBuffer.Write(scratch, 0, read);
            var soFar = headerBuffer.ToArray();
            headerEnd = IndexOfHeaderEnd(soFar, soFar.Length);
            if (headerEnd >= 0)
            {
                break;
            }
        }

        if (headerEnd < 0)
        {
            return null;
        }

        var all = headerBuffer.ToArray();
        var headerText = Encoding.UTF8.GetString(all, 0, headerEnd);
        var preamble = all.AsSpan(headerEnd + 4);

        using var reader = new StringReader(headerText);
        var requestLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return null;
        }

        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var method = parts[0];
        var rawTarget = parts[1];
        var headers = new NameValueCollection();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers.Add(name, value);
        }

        ParseTarget(rawTarget, out var path, out var query);

        long contentLength = 0;
        var lengthRaw = headers["Content-Length"];
        if (!string.IsNullOrWhiteSpace(lengthRaw)
            && long.TryParse(lengthRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLen)
            && parsedLen > 0)
        {
            contentLength = parsedLen;
        }

        if (contentLength > MaxBodyBytes)
        {
            throw new InvalidOperationException($"Request body exceeds {MaxBodyBytes} bytes.");
        }

        var body = new MemoryStream();
        if (preamble.Length > 0)
        {
            body.Write(preamble);
        }

        var remaining = contentLength - body.Length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(scratch.Length, remaining);
            var read = await stream.ReadAsync(scratch.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            body.Write(scratch, 0, read);
            remaining -= read;
        }

        body.Position = 0;

        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        var hostHeader = headers["Host"] ?? "127.0.0.1";
        if (!Uri.TryCreate($"http://{hostHeader}{rawTarget}", UriKind.Absolute, out var url))
        {
            url = new Uri($"http://127.0.0.1{path}");
        }

        var request = new RoomHttpRequest
        {
            Method = method,
            RawTarget = rawTarget,
            Path = path,
            QueryString = query,
            Headers = headers,
            InputStream = body,
            RemoteEndPoint = remote,
            Url = url
        };

        var response = new RoomHttpResponse(stream);
        return new RoomHttpContext
        {
            Request = request,
            Response = response,
            Client = client
        };
    }

    private static void ParseTarget(string rawTarget, out string path, out NameValueCollection query)
    {
        query = new NameValueCollection();
        var q = rawTarget.IndexOf('?', StringComparison.Ordinal);
        path = q >= 0 ? rawTarget[..q] : rawTarget;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = "/";
        }

        if (q < 0 || q >= rawTarget.Length - 1)
        {
            return;
        }

        var queryText = rawTarget[(q + 1)..];
        foreach (var pair in queryText.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                query.Add(Uri.UnescapeDataString(pair.Replace('+', ' ')), string.Empty);
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            query.Add(key, value);
        }
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == (byte)'\r'
                && buffer[i + 1] == (byte)'\n'
                && buffer[i + 2] == (byte)'\r'
                && buffer[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }
}
