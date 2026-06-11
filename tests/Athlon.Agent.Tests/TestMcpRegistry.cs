using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

internal sealed class TestMcpRegistry(IReadOnlyList<McpCatalogEntry>? catalog = null) : IMcpRegistry
{
    private readonly IReadOnlyList<McpCatalogEntry> _catalog = catalog ?? Array.Empty<McpCatalogEntry>();

    public int CatalogVersion => 0;

    public int CatalogCount => _catalog.Count;

    public int CatalogSchemaCharCount => _catalog.Sum(entry =>
        entry.Description.Length + entry.InputSchemaJson.Length + entry.EncodedName.Length);

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
            new Dictionary<string, string> { ["argumentsJson"] = entry.InputSchemaJson },
            Source: "mcp")).ToArray();

    public Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<ToolResult> InvokeAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, string> args,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolResult.Success("ok"));
}
