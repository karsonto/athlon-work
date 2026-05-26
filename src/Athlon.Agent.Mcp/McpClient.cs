namespace Athlon.Agent.Mcp;

public sealed record McpTool(string Name, string Description, string InputSchemaJson);

public sealed record McpServerStatus(string Name, bool Connected, string Transport, IReadOnlyList<McpTool> Tools);

public sealed class McpStdioClient
{
    public Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<McpTool> tools = new[]
        {
            new McpTool("tools/list", "List MCP tools", "{}"),
            new McpTool("tools/call", "Call MCP tool", "{}")
        };
        return Task.FromResult(tools);
    }

    public Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken cancellationToken = default)
    {
        return Task.FromResult($"MCP stdio placeholder invoked {name} with {argumentsJson}");
    }

    public Task<McpServerStatus> GetStatusAsync(string name, CancellationToken cancellationToken = default)
    {
        return ListToolsAsync(cancellationToken)
            .ContinueWith(task => new McpServerStatus(name, Connected: false, Transport: "stdio", task.Result), cancellationToken);
    }
}
