using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Athlon.Agent.Infrastructure.Sso;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Infrastructure;

public interface IMcpRegistry
{
    IReadOnlyList<McpServerStatus> GetStatuses();
    IReadOnlyList<ToolDefinition> ListToolDefinitions();
    IReadOnlyList<McpCatalogEntry> ListCatalogEntries();
    int CatalogVersion { get; }
    int CatalogCount { get; }
    int CatalogSchemaCharCount { get; }
    IReadOnlyList<McpSearchIndex.SearchResult> SearchCatalog(
        string query,
        int topK,
        double minScore,
        string? serverName = null);
    Task RefreshAsync(
        IReadOnlyList<McpServerSettings> settings,
        CancellationToken cancellationToken = default,
        Action? onStatusesChanged = null);
    Task<ToolResult> InvokeAsync(string serverName, string toolName, ToolCallArguments args, CancellationToken cancellationToken = default);
}

public sealed class McpRegistry(IAppLogger logger, IActiveWorkspaceContext workspaceContext) : IMcpRegistry, IAsyncDisposable
{
    private readonly IAppLogger _logger = logger.ForContext("McpRegistry");
    private readonly ConcurrentDictionary<string, IMcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, McpServerStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<McpTool>> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _configFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _toolCallTimeoutSeconds = new(StringComparer.OrdinalIgnoreCase);
    private readonly McpSearchIndexCache _searchIndexCache = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyList<McpCatalogEntry>? _catalogCache;
    private int _catalogVersion;
    private int _disposed;

    public int CatalogVersion => _catalogVersion;
    public int CatalogCount => ListCatalogEntries().Count;
    public int CatalogSchemaCharCount => ListCatalogEntries().Sum(entry =>
        entry.Description.Length + entry.InputSchemaJson.Length + entry.EncodedName.Length);

    public IReadOnlyList<McpServerStatus> GetStatuses() =>
        _statuses.Values.OrderBy(status => status.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<ToolDefinition> ListToolDefinitions()
    {
        var definitions = new List<ToolDefinition>();
        foreach (var (serverName, tools) in _tools)
        {
            foreach (var tool in tools)
            {
                var encoded = McpToolNameCodec.Encode(serverName, tool.Name);
                // ToolDefinition currently only supports string parameters; carry MCP schema in description for now.
                definitions.Add(new ToolDefinition(
                    encoded,
                    string.IsNullOrWhiteSpace(tool.Description) ? $"MCP tool {tool.Name} (server: {serverName})." : tool.Description,
                    ToolSchema.FromMcp(tool.InputSchemaJson),
                    RequiresApproval: false,
                    Source: "mcp"));
            }
        }

        return definitions.OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<McpCatalogEntry> ListCatalogEntries() =>
        _catalogCache ??= BuildCatalogEntries();

    public IReadOnlyList<McpSearchIndex.SearchResult> SearchCatalog(
        string query,
        int topK,
        double minScore,
        string? serverName = null)
    {
        var catalog = ListCatalogEntries();
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            catalog = catalog
                .Where(entry => string.Equals(entry.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return _searchIndexCache.Search(catalog, _catalogVersion, query, topK, minScore);
    }

    private IReadOnlyList<McpCatalogEntry> BuildCatalogEntries()
    {
        var entries = new List<McpCatalogEntry>();
        foreach (var (serverName, tools) in _tools)
        {
            foreach (var tool in tools)
            {
                entries.Add(new McpCatalogEntry(
                    serverName,
                    tool.Name,
                    McpToolNameCodec.Encode(serverName, tool.Name),
                    tool.Description,
                    tool.InputSchemaJson));
            }
        }

        return entries.OrderBy(entry => entry.EncodedName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void InvalidateCatalogCache()
    {
        Interlocked.Increment(ref _catalogVersion);
        _catalogCache = null;
    }

    public async Task RefreshAsync(
        IReadOnlyList<McpServerSettings> settings,
        CancellationToken cancellationToken = default,
        Action? onStatusesChanged = null)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var enabled = settings
                .Where(server => server.Enabled
                    && !string.IsNullOrWhiteSpace(server.Name)
                    && IsValidServerConfig(server)
                    && IsSupportedTransport(server.TransportType))
                .ToDictionary(server => server.Name.Trim(), StringComparer.OrdinalIgnoreCase);

            // Stop disabled/removed servers.
            var removedAny = false;
            foreach (var existing in _clients.Keys.ToArray())
            {
                if (!enabled.ContainsKey(existing) && _clients.TryRemove(existing, out var removed))
                {
                    _tools.TryRemove(existing, out _);
                    _configFingerprints.TryRemove(existing, out _);
                    _toolCallTimeoutSeconds.TryRemove(existing, out _);
                    _statuses[existing] = new McpServerStatus(existing, McpConnectionState.Disabled, "stdio", Array.Empty<McpTool>());
                    try { await removed.DisposeAsync(); } catch { /* ignore */ }
                    RecordMcpServer(existing, "disconnected");
                    removedAny = true;
                }
            }

            if (removedAny)
            {
                onStatusesChanged?.Invoke();
            }

            var toConnect = new List<(string Name, McpServerSettings Server, string Fingerprint)>();
            foreach (var (name, server) in enabled)
            {
                var fingerprint = CreateConfigFingerprint(server);
                _toolCallTimeoutSeconds[name] = Math.Clamp(server.ToolCallTimeoutSeconds, 1, 3600);
                if (_clients.ContainsKey(name)
                    && _configFingerprints.TryGetValue(name, out var existingFingerprint)
                    && string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                if (_clients.TryRemove(name, out var existing))
                {
                    try { await existing.DisposeAsync(); } catch { /* ignore */ }
                }

                var transportLabel = ResolveTransportLabel(server);
                _tools[name] = Array.Empty<McpTool>();
                _statuses[name] = new McpServerStatus(
                    name,
                    McpConnectionState.Connecting,
                    transportLabel,
                    Array.Empty<McpTool>());
                toConnect.Add((name, server, fingerprint));
            }

            if (toConnect.Count > 0)
            {
                onStatusesChanged?.Invoke();
            }

            // Connect independently so one wedged stdio server cannot hide HTTP MCP status.
            var connectTasks = toConnect.Select(item => ConnectOneAsync(
                item.Name,
                item.Server,
                item.Fingerprint,
                onStatusesChanged,
                cancellationToken));
            await Task.WhenAll(connectTasks).ConfigureAwait(false);
        }
        finally
        {
            InvalidateCatalogCache();
            _refreshLock.Release();
        }
    }

    private async Task ConnectOneAsync(
        string name,
        McpServerSettings server,
        string fingerprint,
        Action? onStatusesChanged,
        CancellationToken cancellationToken)
    {
        var transportLabel = ResolveTransportLabel(server);
        IMcpClient client;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(McpClientDefaults.ConnectInitializationTimeout + TimeSpan.FromSeconds(5));

            client = await McpSdkClientFactory.ConnectAsync(
                name,
                WithStdioSsoEnvironment(server),
                workspaceContext.RootPath,
                clientName: "Athlon.Agent",
                connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException
                ? $"Timed out connecting to MCP server ({transportLabel})."
                : ex.Message;
            _logger.Warning("MCP connect failed for {Server}: {Message}", name, message);
            _tools[name] = Array.Empty<McpTool>();
            _statuses[name] = new McpServerStatus(
                name,
                McpConnectionState.Error,
                transportLabel,
                Array.Empty<McpTool>(),
                LastError: message);
            RecordMcpServer(name, "disconnected", errorType: ex.GetType().Name);
            onStatusesChanged?.Invoke();
            return;
        }

        _clients[name] = client;
        _configFingerprints[name] = fingerprint;
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            _tools[name] = tools;
            _statuses[name] = client.Status with { Tools = tools.ToArray() };
            RecordMcpServer(name, "connected", toolCount: tools.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning("MCP refresh failed for {Server}: {Message}", name, ex.Message);
            _tools[name] = Array.Empty<McpTool>();
            _statuses[name] = client.Status with
            {
                State = McpConnectionState.Error,
                Tools = Array.Empty<McpTool>(),
                LastError = ex.Message
            };
            RecordMcpServer(name, "disconnected", errorType: ex.GetType().Name);
        }

        onStatusesChanged?.Invoke();
    }

    private static void RecordMcpServer(string serverName, string action, int? toolCount = null, string? errorType = null)
    {
        try
        {
            BehaviorEventManager.Instance.Record(
                BehaviorEventIds.McpServer,
                BehaviorEventTypes.Event,
                BehaviorEventIds.McpServer,
                new Dictionary<string, object?>
                {
                    ["server_name"] = serverName,
                    ["action"] = action,
                    ["tool_count"] = toolCount,
                    ["error_type"] = errorType
                });
        }
        catch
        {
            // ignore
        }
    }

    public async Task<ToolResult> InvokeAsync(string serverName, string toolName, ToolCallArguments args, CancellationToken cancellationToken = default)
    {
        var catalogTool = _tools.TryGetValue(serverName, out var serverTools)
            ? serverTools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (catalogTool is null)
        {
            return ToolInvocationErrors.Failure(
                "MCP tool schema unavailable",
                new ToolInvocationError(
                    "mcp.schema_not_found",
                    "$",
                    "a tool present in the current MCP catalog",
                    $"{serverName}/{toolName}",
                    "Refresh the MCP catalog and search for the tool again before calling it."));
        }

        var schemaFailure = ValidateArgumentsAgainstSchema(catalogTool.InputSchemaJson, args);
        if (schemaFailure is not null)
        {
            return schemaFailure;
        }

        if (!_clients.TryGetValue(serverName, out var client))
        {
            return ToolResult.Failure("MCP server not available", $"Server '{serverName}' is not enabled or not connected.");
        }

        try
        {
            var argumentsJson = args.ToJsonString();
            argumentsJson = NormalizeMcpArgumentsJson(serverName, toolName, argumentsJson, workspaceContext.RootPath);

            using var timeoutCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(_toolCallTimeoutSeconds.GetValueOrDefault(serverName, 120)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var resultJson = await client.CallToolAsync(toolName, argumentsJson, linkedCts.Token);
            _statuses[serverName] = client.Status;
            if (McpResultIsError(resultJson))
            {
                return ToolResult.Failure($"MCP tool {toolName} failed.", resultJson);
            }

            return ToolResult.Success($"MCP tool {toolName} returned.", resultJson);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _statuses[serverName] = client.Status with
            {
                State = McpConnectionState.Error,
                LastError = $"Tool call timed out after {_toolCallTimeoutSeconds.GetValueOrDefault(serverName, 120)}s."
            };
            return ToolResult.Failure("MCP tool call timed out", _statuses[serverName].LastError ?? "MCP tool call timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is TimeoutException && _clients.TryRemove(serverName, out var deadClient))
            {
                _tools.TryRemove(serverName, out _);
                try { await deadClient.DisposeAsync(); } catch { /* ignore */ }
                _statuses[serverName] = new McpServerStatus(
                    serverName,
                    McpConnectionState.Error,
                    "stdio",
                    Array.Empty<McpTool>(),
                    LastError: $"{ex.Message} (MCP process was restarted; refresh MCP servers.)");
            }
            else
            {
                _statuses[serverName] = client.Status with { State = McpConnectionState.Error, LastError = ex.Message };
            }

            return ToolResult.Failure("MCP tool call failed", ex.Message);
        }
    }

    private static bool IsValidServerConfig(McpServerSettings server) =>
        McpTransportKinds.IsHttp(server.TransportType)
            ? !string.IsNullOrWhiteSpace(server.Url)
            : !string.IsNullOrWhiteSpace(server.Command);

    internal static ToolResult? ValidateArgumentsAgainstSchema(
        string inputSchemaJson,
        ToolCallArguments arguments)
    {
        try
        {
            var validationError = ToolInvocationValidator.Validate(
                ToolSchema.FromMcp(inputSchemaJson),
                arguments);
            return validationError is null
                ? null
                : ToolInvocationErrors.Failure("Invalid MCP tool arguments", validationError);
        }
        catch (JsonException ex)
        {
            return ToolInvocationErrors.Failure(
                "MCP tool schema invalid",
                new ToolInvocationError(
                    "mcp.schema_invalid",
                    "$",
                    "valid JSON Schema from the MCP catalog",
                    ex.Message,
                    "Refresh or fix the MCP server schema before retrying the call."));
        }
    }

    private static bool IsSupportedTransport(string? transportType) =>
        McpTransportKinds.IsStdio(transportType) || McpTransportKinds.IsHttp(transportType);

    private static string CreateConfigFingerprint(McpServerSettings server) =>
        JsonSerializer.Serialize(server, JsonFileStore.Options);

    /// <summary>
    /// Inject SSO <c>MCP_REFRESH_TOKEN</c> into stdio MCP process env (same as <see cref="WindowsCmdEncoding"/>).
    /// Fingerprint stays on the original settings so token rotation alone does not force reconnect.
    /// </summary>
    internal static McpServerSettings WithStdioSsoEnvironment(McpServerSettings server)
    {
        if (McpTransportKinds.IsHttp(server.TransportType))
        {
            return server;
        }

        var env = new Dictionary<string, string>(server.Env, StringComparer.Ordinal);
        SsoEenoEnvironment.TryApply(env);
        if (!env.ContainsKey(SsoEenoEnvironment.EnvVarName))
        {
            return server;
        }

        return new McpServerSettings
        {
            Name = server.Name,
            Enabled = server.Enabled,
            TransportType = server.TransportType,
            Url = server.Url,
            Command = server.Command,
            Args = server.Args.ToList(),
            Env = env,
            Headers = new Dictionary<string, string>(server.Headers),
            WorkingDirectory = server.WorkingDirectory,
            ToolCallTimeoutSeconds = server.ToolCallTimeoutSeconds
        };
    }

    private static string NormalizeMcpArgumentsJson(string serverName, string toolName, string argumentsJson, string? workspaceRoot)
    {
        if (!string.Equals(serverName, "qwen-vision", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(toolName, "analyze_image", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return argumentsJson;
        }

        try
        {
            var node = JsonNode.Parse(argumentsJson) as JsonObject;
            var image = node?["image"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(image) || Path.IsPathFullyQualified(image))
            {
                return argumentsJson;
            }

            node!["image"] = Path.GetFullPath(Path.Combine(workspaceRoot, image));
            return node.ToJsonString();
        }
        catch
        {
            return argumentsJson;
        }
    }

    private static bool McpResultIsError(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            return doc.RootElement.TryGetProperty("isError", out var isError)
                && isError.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveTransportLabel(McpServerSettings server)
    {
        if (!McpTransportKinds.IsHttp(server.TransportType))
        {
            return "stdio";
        }

        var mode = McpTransportKinds.ResolveHttpTransportMode(server.TransportType, server.Url);
        return McpTransportKinds.FormatHttpTransportLabel(mode);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (var client in _clients.Values)
        {
            try { await client.DisposeAsync(); } catch { /* ignore */ }
        }
        _clients.Clear();
        _tools.Clear();
        _statuses.Clear();
        _refreshLock.Dispose();
    }
}

