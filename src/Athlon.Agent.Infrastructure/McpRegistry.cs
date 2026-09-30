using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.RuntimeDiagnostics;
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
    /// <summary>
    /// Force-reconnect a single enabled server, ignoring config fingerprint skip logic.
    /// </summary>
    Task ReconnectAsync(
        string serverName,
        IReadOnlyList<McpServerSettings> settings,
        CancellationToken cancellationToken = default,
        Action? onStatusesChanged = null);
    Task<ToolResult> InvokeAsync(string serverName, string toolName, ToolCallArguments args, CancellationToken cancellationToken = default);
}

public sealed partial class McpRegistry(
    IAppLogger logger,
    IActiveWorkspaceContext workspaceContext,
    IAgentRunContextAccessor runContextAccessor,
    IRuntimeDiagnosticEventSink runtimeDiagnosticEventSink) : IMcpRegistry, IAsyncDisposable
{
    private readonly IAppLogger _logger = logger.ForContext("McpRegistry");
    private readonly IAgentRunContextAccessor _runContextAccessor = runContextAccessor;
    private readonly IRuntimeDiagnosticEventSink _runtimeDiagnosticEventSink = runtimeDiagnosticEventSink;
    private readonly ConcurrentDictionary<string, IMcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, McpServerStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<McpTool>> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _configFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _toolCallTimeoutSeconds = new(StringComparer.OrdinalIgnoreCase);
    private readonly McpSearchIndexCache _searchIndexCache = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _connectGenerations = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<McpCatalogEntry>? _catalogCache;
    private int _catalogSchemaChars;
    private int _catalogVersion;
    private int _disposed;

    public int CatalogVersion => _catalogVersion;
    public int CatalogCount => ListCatalogEntries().Count;
    public int CatalogSchemaCharCount
    {
        get
        {
            // Fast path: unscoped access returns the cached full-catalog count.
            if (ScheduleTurnScope.Current?.McpServerNames is null && _catalogCache is not null)
            {
                return _catalogSchemaChars;
            }

            return ListCatalogEntries().Sum(entry =>
                entry.Description.Length + entry.InputSchemaJson.Length + entry.EncodedName.Length);
        }
    }

    public IReadOnlyList<McpServerStatus> GetStatuses() =>
        _statuses.Values.OrderBy(status => status.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<ToolDefinition> ListToolDefinitions()
    {
        var definitions = new List<ToolDefinition>();
        foreach (var (serverName, tools) in _tools)
        {
            if (!ScheduleTurnScope.IsMcpServerAllowed(serverName))
            {
                continue;
            }

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

    public IReadOnlyList<McpCatalogEntry> ListCatalogEntries()
    {
        var entries = _catalogCache ??= BuildCatalogEntries();
        if (ScheduleTurnScope.Current?.McpServerNames is null)
        {
            return entries;
        }

        return entries
            .Where(entry => ScheduleTurnScope.IsMcpServerAllowed(entry.ServerName))
            .ToArray();
    }

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

        var sorted = entries.OrderBy(entry => entry.EncodedName, StringComparer.OrdinalIgnoreCase).ToArray();
        _catalogSchemaChars = ComputeCatalogSchemaChars(sorted);
        return sorted;
    }

    private static int ComputeCatalogSchemaChars(IReadOnlyList<McpCatalogEntry> entries)
    {
        var total = 0;
        foreach (var entry in entries)
        {
            total += entry.Description.Length + entry.InputSchemaJson.Length + entry.EncodedName.Length;
        }

        return total;
    }

    private void InvalidateCatalogCache()
    {
        Interlocked.Increment(ref _catalogVersion);
        _catalogCache = null;
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
