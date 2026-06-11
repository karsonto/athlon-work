using System.Text.Json;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

internal static class McpSearchGatewayTools
{
    public const string SearchToolName = "mcp_search";
    public const string DescribeToolName = "mcp_describe";
    public const string CallToolName = "mcp_call";
    public const string RefreshCatalogToolName = "mcp_refresh_catalog";

    public static IReadOnlyList<IAgentTool> Create(IMcpRegistry registry, AppSettings settings, Func<Task> refreshCatalogAsync)
    {
        var searchSettings = settings.McpSearch;
        return
        [
            new GatewayTool(
                SearchToolName,
                "Search connected MCP tools by natural-language intent, server, action, and parameter names.",
                new Dictionary<string, string>
                {
                    ["query"] = "The user intent or task to find MCP tools for.",
                    ["topK"] = "Optional maximum number of matching tools to return.",
                    ["serverName"] = "Optional MCP server name to search within."
                },
                async invocation =>
                {
                    var query = invocation.Arguments.GetValueOrDefault("query") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(query))
                    {
                        return ToolResult.Failure("Missing query", "query is required");
                    }

                    var topK = ParseTopK(invocation.Arguments.GetValueOrDefault("topK"), searchSettings);
                    var serverName = invocation.Arguments.GetValueOrDefault("serverName");
                    var catalog = registry.ListCatalogEntries();
                    if (!string.IsNullOrWhiteSpace(serverName))
                    {
                        catalog = catalog
                            .Where(entry => string.Equals(entry.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                    }

                    var results = McpSearchIndex.Search(catalog, query, topK, searchSettings.MinScore);
                    var payload = results.Select(result => new
                    {
                        toolId = result.Entry.EncodedName,
                        serverName = result.Entry.ServerName,
                        toolName = result.Entry.ToolName,
                        description = result.Entry.Description,
                        score = Math.Round(result.Score, 3),
                        matchedKeywords = result.MatchedKeywords
                    }).ToArray();

                    return ToolResult.Success(
                        $"Found {payload.Length} MCP tool(s) for query.",
                        JsonSerializer.Serialize(new
                        {
                            query,
                            totalIndexed = registry.ListCatalogEntries().Count,
                            searchedTools = catalog.Count,
                            results = payload
                        }));
                }),
            new GatewayTool(
                DescribeToolName,
                "Return the full schema and metadata for a connected MCP tool found by mcp_search.",
                new Dictionary<string, string> { ["toolId"] = "Encoded MCP tool id from mcp_search." },
                async invocation =>
                {
                    var toolId = invocation.Arguments.GetValueOrDefault("toolId") ?? string.Empty;
                    var entry = registry.ListCatalogEntries()
                        .FirstOrDefault(item => string.Equals(item.EncodedName, toolId, StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                    {
                        return ToolResult.Failure("Unknown MCP tool", $"unknown MCP tool: {toolId}");
                    }

                    return ToolResult.Success(
                        $"Described MCP tool {entry.EncodedName}.",
                        JsonSerializer.Serialize(new
                        {
                            toolId = entry.EncodedName,
                            serverName = entry.ServerName,
                            toolName = entry.ToolName,
                            description = entry.Description,
                            inputSchemaJson = entry.InputSchemaJson
                        }));
                }),
            new GatewayTool(
                CallToolName,
                "Call a connected MCP tool by encoded tool id with JSON arguments.",
                new Dictionary<string, string>
                {
                    ["toolId"] = "Encoded MCP tool id from mcp_search.",
                    ["argumentsJson"] = "JSON arguments matching the MCP tool input schema."
                },
                async invocation =>
                {
                    var toolId = invocation.Arguments.GetValueOrDefault("toolId") ?? string.Empty;
                    if (!McpToolNameCodec.TryDecode(toolId, out var serverName, out var toolName))
                    {
                        return ToolResult.Failure("Invalid tool id", $"unknown MCP tool: {toolId}");
                    }

                    var argumentsJson = invocation.Arguments.GetValueOrDefault("argumentsJson") ?? "{}";
                    return await registry.InvokeAsync(
                        serverName,
                        toolName,
                        new Dictionary<string, string> { ["argumentsJson"] = argumentsJson });
                }),
            new GatewayTool(
                RefreshCatalogToolName,
                "Refresh the MCP tool catalog and rebuild the local search index.",
                new Dictionary<string, string>(),
                async _ =>
                {
                    await refreshCatalogAsync();
                    var catalog = registry.ListCatalogEntries();
                    return ToolResult.Success(
                        "MCP catalog refreshed.",
                        JsonSerializer.Serialize(new
                        {
                            totalIndexed = catalog.Count,
                            refreshedAt = DateTimeOffset.UtcNow
                        }));
                })
        ];
    }

    private static int ParseTopK(string? raw, McpSearchSettings settings)
    {
        if (!int.TryParse(raw, out var topK) || topK <= 0)
        {
            return settings.TopKDefault;
        }

        return Math.Min(topK, settings.TopKMax);
    }

    private sealed class GatewayTool(
        string name,
        string description,
        IReadOnlyDictionary<string, string> parameters,
        Func<ToolInvocation, Task<ToolResult>> execute) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(name, description, parameters, RequiresApproval: false, Source: "mcp-search");

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            execute(invocation);
    }
}
