namespace Athlon.Agent.Mcp;

public enum McpConnectionState
{
    Disabled,
    Connecting,
    Connected,
    Error
}

public sealed record McpTool(string Name, string Description, string InputSchemaJson);

public sealed record McpServerStatus(
    string Name,
    McpConnectionState State,
    string Transport,
    IReadOnlyList<McpTool> Tools,
    string? LastError = null);

public interface IMcpClient : IAsyncDisposable
{
    string Name { get; }
    McpServerStatus Status { get; }

    Task InitializeAsync(string? clientName = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default);
}
