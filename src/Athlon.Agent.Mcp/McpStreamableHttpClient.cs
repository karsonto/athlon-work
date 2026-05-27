using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Athlon.Agent.Mcp;

/// <summary>
/// MCP Streamable HTTP transport: JSON-RPC over HTTP POST to a single endpoint,
/// with <c>application/json</c> or <c>text/event-stream</c> responses.
/// </summary>
public sealed class McpStreamableHttpClient : IMcpClient
{
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly TimeSpan _defaultTimeout;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long _nextId;
    private string? _sessionId;
    private volatile bool _disposed;

    private McpConnectionState _state = McpConnectionState.Disabled;
    private List<McpTool> _tools = new();
    private string? _lastError;

    public McpStreamableHttpClient(
        string name,
        string endpointUrl,
        IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? defaultTimeout = null,
        HttpMessageHandler? handler = null)
    {
        Name = name;
        _endpoint = new Uri(endpointUrl, UriKind.Absolute);
        _headers = headers ?? new Dictionary<string, string>();
        _defaultTimeout = defaultTimeout ?? McpClientDefaults.RequestTimeout;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = _defaultTimeout;
    }

    public string Name { get; }

    public McpServerStatus Status => new(
        Name,
        _state,
        Transport: "streamable-http",
        Tools: _tools.ToArray(),
        LastError: _lastError);

    public async Task InitializeAsync(string? clientName = null, CancellationToken cancellationToken = default)
    {
        _state = McpConnectionState.Connecting;
        try
        {
            var payload = McpJsonRpc.CreateInitializeParams(clientName);

            _ = await SendRequestAsync("initialize", payload, cancellationToken);
            await SendInitializedNotificationAsync(cancellationToken);
            _state = McpConnectionState.Connected;
            _lastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state = McpConnectionState.Error;
            _lastError = ex.Message;
            throw;
        }
    }

    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync("tools/list", null, cancellationToken);
        var parsed = McpToolParser.ParseTools(result);
        _tools = parsed.ToList();
        return parsed;
    }

    public async Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        var argsNode = McpToolParser.ParseArgumentsNode(argumentsJson);

        var payload = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = argsNode ?? new JsonObject()
        };

        var result = await SendRequestAsync("tools/call", payload, cancellationToken);
        return result.GetRawText();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _httpClient.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<JsonElement> SendRequestAsync(string method, JsonNode? payload, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(McpStreamableHttpClient));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            var requestJson = McpJsonRpc.BuildPostBody(McpJsonRpc.CreateRequest(id, method, payload));

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };

            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", McpJsonRpc.ProtocolVersion);

            if (!string.IsNullOrWhiteSpace(_sessionId))
            {
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            }

            foreach (var (key, value) in _headers)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                request.Headers.TryAddWithoutValidation(key, value);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"MCP HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }

            if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
            {
                _sessionId = sessionIds.FirstOrDefault();
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                return McpJsonRpc.ParseResultFromSseBody(body, id);
            }

            return McpJsonRpc.ParseResultFromJsonBody(body, id);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task SendInitializedNotificationAsync(CancellationToken cancellationToken) =>
        SendNotificationAsync("notifications/initialized", cancellationToken);

    private async Task SendNotificationAsync(string method, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(McpStreamableHttpClient));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var notificationJson = McpJsonRpc.BuildPostBody(McpJsonRpc.CreateNotification(method));
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(notificationJson, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", McpJsonRpc.ProtocolVersion);
            if (!string.IsNullOrWhiteSpace(_sessionId))
            {
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            }

            foreach (var (key, value) in _headers)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    request.Headers.TryAddWithoutValidation(key, value);
                }
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"MCP notification HTTP {(int)response.StatusCode}: {body}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
