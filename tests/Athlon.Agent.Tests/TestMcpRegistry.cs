using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Tests;

internal sealed class TestMcpRegistry(IReadOnlyList<McpCatalogEntry>? catalog = null) : IMcpRegistry
{
    private IReadOnlyList<McpCatalogEntry> _catalog = catalog ?? Array.Empty<McpCatalogEntry>();

    public int InvocationCount { get; private set; }
    public string? LastServerName { get; private set; }
    public string? LastToolName { get; private set; }
    public ToolCallArguments? LastArguments { get; private set; }

    public int CatalogVersion => 0;

    public int CatalogCount => _catalog.Count;

    public int CatalogSchemaCharCount => _catalog.Sum(entry =>
        entry.Description.Length + entry.InputSchemaJson.Length + entry.EncodedName.Length);

    public void SetCatalog(IReadOnlyList<McpCatalogEntry> catalog) =>
        _catalog = catalog ?? Array.Empty<McpCatalogEntry>();

    public IReadOnlyList<McpCatalogEntry> ListCatalogEntries() => _catalog;

    public IReadOnlyList<McpSearchIndex.SearchResult> SearchCatalog(
        string query,
        int topK,
        double minScore,
        string? serverName = null)
    {
        var catalog = string.IsNullOrWhiteSpace(serverName)
            ? _catalog
            : _catalog.Where(entry => string.Equals(entry.ServerName, serverName, StringComparison.OrdinalIgnoreCase)).ToArray();
        return McpSearchIndex.Search(catalog, query, topK, minScore);
    }

    public IReadOnlyList<McpServerStatus> GetStatuses() => Array.Empty<McpServerStatus>();

    public IReadOnlyList<ToolDefinition> ListToolDefinitions() =>
        _catalog.Select(entry => new ToolDefinition(
            entry.EncodedName,
            entry.Description,
            ToolSchema.FromMcp(entry.InputSchemaJson),
            Source: "mcp")).ToArray();

    public Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default, Action? onStatusesChanged = null) =>
        Task.CompletedTask;

    public Task<ToolResult> InvokeAsync(
        string serverName,
        string toolName,
        ToolCallArguments args,
        CancellationToken cancellationToken = default)
    {
        InvocationCount++;
        LastServerName = serverName;
        LastToolName = toolName;
        LastArguments = args;
        return Task.FromResult(ToolResult.Success("ok"));
    }
}
