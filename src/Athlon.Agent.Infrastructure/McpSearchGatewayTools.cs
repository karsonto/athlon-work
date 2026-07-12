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
                "Search connected MCP tools by natural-language intent. In search mode, always call this before mcp_call when the target MCP tool is not already known.",
                ToolSchema.Object()
                    .String("query", "The user intent or task to find MCP tools for.", required: true, minLength: 1)
                    .Integer("topK", "Maximum number of matching tools to return.", minimum: 1, maximum: searchSettings.TopKMax)
                    .String("serverName", "MCP server name to search within.")
                    .Build(),
                async invocation =>
                {
                    var query = invocation.Arguments.GetString("query") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(query))
                    {
                        return ToolResult.Failure("Missing query", "query is required");
                    }

                    var topK = ParseTopK(invocation.Arguments, searchSettings);
                    var serverName = invocation.Arguments.GetString("serverName");
                    var results = registry.SearchCatalog(query, topK, searchSettings.MinScore, serverName);
                    var catalog = string.IsNullOrWhiteSpace(serverName)
                        ? registry.ListCatalogEntries()
                        : registry.ListCatalogEntries()
                            .Where(entry => string.Equals(entry.ServerName, serverName, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                    var payload = results.Select(result =>
                    {
                        var schema = McpSchemaPresenter.Present(result.Entry.InputSchemaJson);
                        return new
                        {
                            toolId = result.Entry.EncodedName,
                            serverName = result.Entry.ServerName,
                            toolName = result.Entry.ToolName,
                            description = result.Entry.Description,
                            score = Math.Round(result.Score, 3),
                            matchedKeywords = result.MatchedKeywords,
                            inputSchema = schema.InputSchema,
                            schemaFingerprint = schema.Fingerprint,
                            requiresDescribe = schema.RequiresDescribe,
                            schemaTruncated = schema.Truncated,
                            guidance = schema.Guidance
                        };
                    }).ToArray();

                    return ToolResult.Success(
                        $"Found {payload.Length} MCP tool(s) for query.",
                        JsonSerializer.Serialize(new
                        {
                            query,
                            totalIndexed = registry.CatalogCount,
                            searchedTools = catalog.Count,
                            results = payload
                        }));
                }),
            new GatewayTool(
                DescribeToolName,
                "Return the full schema and metadata for a connected MCP tool found by mcp_search.",
                ToolSchema.Object()
                    .String("toolId", "Encoded MCP tool id from mcp_search.", required: true, minLength: 1)
                    .Build(),
                async invocation =>
                {
                    var toolId = invocation.Arguments.GetString("toolId") ?? string.Empty;
                    var entry = registry.ListCatalogEntries()
                        .FirstOrDefault(item => string.Equals(item.EncodedName, toolId, StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                    {
                        return ToolResult.Failure("Unknown MCP tool", $"unknown MCP tool: {toolId}");
                    }

                    return ToolResult.Success(
                        $"Described MCP tool {entry.EncodedName}.",
                        BuildDescribePayload(entry));
                }),
            new GatewayTool(
                CallToolName,
                "Call a connected MCP tool by encoded tool id with native structured arguments.",
                ToolSchema.Object()
                    .String("toolId", "Encoded MCP tool id from mcp_search.", required: true, minLength: 1)
                    .Object(
                        "arguments",
                        "Native JSON object matching the MCP tool input schema.",
                        ToolSchema.Object(additionalProperties: true).Build(),
                        required: true)
                    .Build(),
                async invocation =>
                {
                    var toolId = invocation.Arguments.GetString("toolId") ?? string.Empty;
                    if (!McpToolNameCodec.TryDecode(toolId, out var serverName, out var toolName))
                    {
                        return ToolResult.Failure("Invalid tool id", $"unknown MCP tool: {toolId}");
                    }

                    if (!invocation.Arguments.TryGetObject("arguments", out var arguments))
                    {
                        return ToolResult.Failure("Invalid arguments", "mcp_call requires a native `arguments` object.");
                    }

                    return await registry.InvokeAsync(
                        serverName,
                        toolName,
                        ToolCallArguments.FromJsonElement(arguments));
                }),
            new GatewayTool(
                RefreshCatalogToolName,
                "Refresh the MCP tool catalog and rebuild the local search index.",
                ToolSchema.Object().Build(),
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

    private static int ParseTopK(ToolCallArguments arguments, McpSearchSettings settings)
    {
        if (!arguments.TryGetInt32("topK", out var topK) || topK <= 0)
        {
            return settings.TopKDefault;
        }

        return Math.Min(topK, settings.TopKMax);
    }

    private static string BuildDescribePayload(McpCatalogEntry entry)
    {
        var presentation = McpSchemaPresenter.Present(entry.InputSchemaJson);
        using var document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(entry.InputSchemaJson) ? "{}" : entry.InputSchemaJson);
        return JsonSerializer.Serialize(new
        {
            toolId = entry.EncodedName,
            serverName = entry.ServerName,
            toolName = entry.ToolName,
            description = entry.Description,
            inputSchema = document.RootElement,
            schemaFingerprint = presentation.Fingerprint,
            schemaComplete = true,
            guidance = "Use mcp_call with native `arguments`; do not JSON-stringify the object."
        });
    }

    private sealed class GatewayTool(
        string name,
        string description,
        ToolJsonSchema parametersSchema,
        Func<ToolInvocation, Task<ToolResult>> execute) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(name, description, parametersSchema, RequiresApproval: false, Source: "mcp-search");

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            execute(invocation);
    }
}
