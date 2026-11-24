using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athlon.Agent.App.Services.Room;

/// <summary>Minimal Docker Engine API client (named pipe / unix socket).</summary>
internal sealed class RoomDockerEngineClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly IDisposable _handler;

    private RoomDockerEngineClient(HttpClient http, IDisposable handler)
    {
        _http = http;
        _handler = handler;
    }

    public static RoomDockerEngineClient Create()
    {
        if (OperatingSystem.IsWindows())
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, ct) =>
                {
                    var pipe = new NamedPipeClientStream(
                        ".",
                        "docker_engine",
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
                    return pipe;
                }
            };
            var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost/"),
                Timeout = TimeSpan.FromMinutes(10)
            };
            return new RoomDockerEngineClient(http, handler);
        }

        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), ct)
                            .ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost/"),
                Timeout = TimeSpan.FromMinutes(10)
            };
            return new RoomDockerEngineClient(http, handler);
        }
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("_ping", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("version", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("Version", out var v))
        {
            return v.GetString();
        }

        if (doc.RootElement.TryGetProperty("version", out var v2))
        {
            return v2.GetString();
        }

        return null;
    }

    public async Task<bool> ImageExistsAsync(string imageRef, CancellationToken cancellationToken)
    {
        var encoded = Uri.EscapeDataString(imageRef);
        using var response = await _http.GetAsync($"images/{encoded}/json", cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async Task BuildImageAsync(
        Stream tarContext,
        string tag,
        string dockerfileName,
        CancellationToken cancellationToken)
    {
        var query = $"build?t={Uri.EscapeDataString(tag)}&dockerfile={Uri.EscapeDataString(dockerfileName)}&q=1";
        using var content = new StreamContent(tarContext);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");
        using var response = await _http.PostAsync(query, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Docker build failed ({(int)response.StatusCode}): {Truncate(body)}");
        }

        // Build streams JSON lines; check for errorDetail
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("error", out var err)
                    || doc.RootElement.TryGetProperty("errorDetail", out _))
                {
                    var msg = err.ValueKind == JsonValueKind.String
                        ? err.GetString()
                        : line;
                    throw new InvalidOperationException($"Docker build error: {msg}");
                }
            }
            catch (JsonException)
            {
                // ignore non-json
            }
        }
    }

    public async Task<string> CreateContainerAsync(
        object createBody,
        CancellationToken cancellationToken,
        string? name = null)
    {
        var json = JsonSerializer.Serialize(createBody, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var url = string.IsNullOrWhiteSpace(name)
            ? "containers/create"
            : $"containers/create?name={Uri.EscapeDataString(name)}";
        using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Container create failed ({(int)response.StatusCode}): {Truncate(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("Id", out var idEl))
        {
            throw new InvalidOperationException("Container create response missing Id.");
        }

        return idEl.GetString() ?? throw new InvalidOperationException("Container Id empty.");
    }

    public async Task StartContainerAsync(string containerId, CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsync($"containers/{containerId}/start", content: null, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified or HttpStatusCode.OK)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException($"Container start failed ({(int)response.StatusCode}): {Truncate(body)}");
    }

    public async Task StopContainerAsync(string containerId, CancellationToken cancellationToken)
    {
        using var response = await _http
            .PostAsync($"containers/{containerId}/stop?t=5", content: null, cancellationToken)
            .ConfigureAwait(false);
        // 304/404 are fine
    }

    public async Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken)
    {
        using var response = await _http
            .DeleteAsync($"containers/{containerId}?force=1&v=1", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string?> FindContainerIdByNameAsync(string name, CancellationToken cancellationToken)
    {
        var filters = Uri.EscapeDataString($"{{\"name\":[\"^{name}$\"]}}");
        using var response = await _http
            .GetAsync($"containers/json?all=1&filters={filters}", cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var first = doc.RootElement[0];
        return first.TryGetProperty("Id", out var id) ? id.GetString() : null;
    }

    public async Task<bool> IsContainerRunningAsync(string containerId, CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync($"containers/{containerId}/json", cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("State", out var state)
            && state.TryGetProperty("Running", out var runningEl))
        {
            return runningEl.GetBoolean();
        }

        return false;
    }

    public async Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
        string containerId,
        IReadOnlyList<string> cmd,
        string workingDir,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var createPayload = new Dictionary<string, object?>
        {
            ["AttachStdout"] = true,
            ["AttachStderr"] = true,
            ["Cmd"] = cmd.ToArray(),
            ["WorkingDir"] = workingDir,
            ["User"] = "athlon"
        };
        var createJson = JsonSerializer.Serialize(createPayload, JsonOptions);
        using (var createContent = new StringContent(createJson, Encoding.UTF8, "application/json"))
        using (var createResponse = await _http
                   .PostAsync($"containers/{containerId}/exec", createContent, cancellationToken)
                   .ConfigureAwait(false))
        {
            var createBody = await createResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!createResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Exec create failed ({(int)createResponse.StatusCode}): {Truncate(createBody)}");
            }

            using var createDoc = JsonDocument.Parse(createBody);
            var execId = createDoc.RootElement.GetProperty("Id").GetString()
                ?? throw new InvalidOperationException("Exec Id missing.");

            using var startContent = new StringContent(
                """{"Detach":false,"Tty":false}""",
                Encoding.UTF8,
                "application/json");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)));

            using var startResponse = await _http
                .PostAsync($"exec/{execId}/start", startContent, timeoutCts.Token)
                .ConfigureAwait(false);
            var multiplexed = await startResponse.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            var (stdout, stderr) = DemuxDockerStream(multiplexed);

            using var inspectResponse = await _http
                .GetAsync($"exec/{execId}/json", cancellationToken)
                .ConfigureAwait(false);
            var inspectBody = await inspectResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var exitCode = 0;
            if (inspectResponse.IsSuccessStatusCode)
            {
                using var inspectDoc = JsonDocument.Parse(inspectBody);
                if (inspectDoc.RootElement.TryGetProperty("ExitCode", out var codeEl))
                {
                    exitCode = codeEl.GetInt32();
                }
            }

            return (exitCode, stdout, stderr);
        }
    }

    public async Task PutArchiveAsync(
        string containerId,
        string containerDirectory,
        Stream tarStream,
        CancellationToken cancellationToken)
    {
        var path = Uri.EscapeDataString(containerDirectory);
        using var content = new StreamContent(tarStream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");
        using var response = await _http
            .PutAsync($"containers/{containerId}/archive?path={path}", content, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Put archive failed ({(int)response.StatusCode}): {Truncate(body)}");
        }
    }

    public async Task<byte[]?> GetArchiveAsync(
        string containerId,
        string containerPath,
        CancellationToken cancellationToken)
    {
        var path = Uri.EscapeDataString(containerPath);
        using var response = await _http
            .GetAsync($"containers/{containerId}/archive?path={path}", cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Get archive failed ({(int)response.StatusCode}): {Truncate(body)}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _handler.Dispose();
        return ValueTask.CompletedTask;
    }

    private static (string Stdout, string Stderr) DemuxDockerStream(byte[] data)
    {
        // Docker multiplexed stream: [8-byte header][payload]...
        // header: stream type (1=stdout, 2=stderr) + 3 zero + uint32 BE size
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            var streamType = data[offset];
            var size = (data[offset + 4] << 24) | (data[offset + 5] << 16) | (data[offset + 6] << 8) | data[offset + 7];
            offset += 8;
            if (size < 0 || offset + size > data.Length)
            {
                break;
            }

            var target = streamType == 2 ? stderr : stdout;
            target.Write(data, offset, size);
            offset += size;
        }

        if (offset == 0 && data.Length > 0)
        {
            // Non-multiplexed fallback
            return (Encoding.UTF8.GetString(data), string.Empty);
        }

        return (Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
    }

    private static string Truncate(string text, int max = 800) =>
        text.Length <= max ? text : text[..max] + "…";
}
