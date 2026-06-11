using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Athlon.Agent.Core;
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
    Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default);
    Task<ToolResult> InvokeAsync(string serverName, string toolName, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken = default);
}

public sealed class McpRegistry(IAppLogger logger, IActiveWorkspaceContext workspaceContext) : IMcpRegistry, IAsyncDisposable
{
    private readonly IAppLogger _logger = logger.ForContext("McpRegistry");
    private readonly ConcurrentDictionary<string, IMcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, McpServerStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<McpTool>> _tools = new(StringComparer.OrdinalIgnoreCase);
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
                    new Dictionary<string, string>
                    {
                        ["argumentsJson"] = $"JSON string for MCP inputSchema: {tool.InputSchemaJson}"
                    },
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

    public async Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default)
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
            foreach (var existing in _clients.Keys.ToArray())
            {
                if (!enabled.ContainsKey(existing) && _clients.TryRemove(existing, out var removed))
                {
                    _tools.TryRemove(existing, out _);
                    _statuses[existing] = new McpServerStatus(existing, McpConnectionState.Disabled, "stdio", Array.Empty<McpTool>());
                    try { await removed.DisposeAsync(); } catch { /* ignore */ }
                }
            }

            // Start/refresh enabled servers (always recreate so saved config changes apply).
            foreach (var (name, server) in enabled)
            {
                if (_clients.TryRemove(name, out var existing))
                {
                    try { await existing.DisposeAsync(); } catch { /* ignore */ }
                }

                IMcpClient client;
                try
                {
                    client = await McpSdkClientFactory.ConnectAsync(
                        name,
                        server,
                        workspaceContext.RootPath,
                        clientName: "Athlon.Agent",
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning("MCP connect failed for {Server}: {Message}", name, ex.Message);
                    _tools[name] = Array.Empty<McpTool>();
                    _statuses[name] = new McpServerStatus(
                        name,
                        McpConnectionState.Error,
                        ResolveTransportLabel(server),
                        Array.Empty<McpTool>(),
                        LastError: ex.Message);
                    continue;
                }

                _clients[name] = client;
                try
                {
                    var tools = await client.ListToolsAsync(cancellationToken);
                    _tools[name] = tools;
                    _statuses[name] = client.Status with { Tools = tools.ToArray() };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning("MCP refresh failed for {Server}: {Message}", name, ex.Message);
                    _tools[name] = Array.Empty<McpTool>();
                    _statuses[name] = client.Status with { State = McpConnectionState.Error, Tools = Array.Empty<McpTool>(), LastError = ex.Message };
                }
            }
        }
        finally
        {
            InvalidateCatalogCache();
            _refreshLock.Release();
        }
    }

    public async Task<ToolResult> InvokeAsync(string serverName, string toolName, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(serverName, out var client))
        {
            return ToolResult.Failure("MCP server not available", $"Server '{serverName}' is not enabled or not connected.");
        }

        try
        {
            // Prefer explicit argumentsJson if provided; otherwise serialize the argument dictionary.
            var argumentsJson = args.TryGetValue("argumentsJson", out var explicitJson) && !string.IsNullOrWhiteSpace(explicitJson)
                ? explicitJson
                : JsonSerializer.Serialize(args);
            argumentsJson = NormalizeMcpArgumentsJson(serverName, toolName, argumentsJson, workspaceContext.RootPath);

            var resultJson = await client.CallToolAsync(toolName, argumentsJson, cancellationToken);
            _statuses[serverName] = client.Status;
            if (McpResultIsError(resultJson))
            {
                return ToolResult.Failure($"MCP tool {toolName} failed.", resultJson);
            }

            return ToolResult.Success($"MCP tool {toolName} returned.", resultJson);
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
        McpTransportKinds.IsStreamableHttp(server.TransportType)
            ? !string.IsNullOrWhiteSpace(server.Url)
            : !string.IsNullOrWhiteSpace(server.Command);

    private static bool IsSupportedTransport(string? transportType) =>
        McpTransportKinds.IsStdio(transportType) || McpTransportKinds.IsStreamableHttp(transportType);

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

    private static string ResolveTransportLabel(McpServerSettings server) =>
        McpTransportKinds.IsStreamableHttp(server.TransportType) ? "streamable-http" : "stdio";

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

