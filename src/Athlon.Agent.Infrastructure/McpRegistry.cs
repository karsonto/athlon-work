using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Mcp;

namespace Athlon.Agent.Infrastructure;

public interface IMcpRegistry
{
    IReadOnlyList<McpServerStatus> GetStatuses();
    IReadOnlyList<ToolDefinition> ListToolDefinitions();
    Task RefreshAsync(IReadOnlyList<McpServerSettings> settings, CancellationToken cancellationToken = default);
    Task<ToolResult> InvokeAsync(string serverName, string toolName, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken = default);
}

public sealed class McpRegistry(IAppLogger logger) : IMcpRegistry, IAsyncDisposable
{
    private readonly IAppLogger _logger = logger.ForContext("McpRegistry");
    private readonly ConcurrentDictionary<string, IMcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, McpServerStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<McpTool>> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

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

            // Start/refresh enabled servers.
            foreach (var (name, server) in enabled)
            {
                var client = _clients.GetOrAdd(name, _ => CreateClient(name, server));
                try
                {
                    await client.InitializeAsync(clientName: "Athlon.Agent", cancellationToken);
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

            var resultJson = await client.CallToolAsync(toolName, argumentsJson, cancellationToken);
            _statuses[serverName] = client.Status;
            return ToolResult.Success($"MCP tool {toolName} returned.", resultJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _statuses[serverName] = client.Status with { State = McpConnectionState.Error, LastError = ex.Message };
            return ToolResult.Failure("MCP tool call failed", ex.Message);
        }
    }

    private static bool IsValidServerConfig(McpServerSettings server) =>
        McpTransportKinds.IsStreamableHttp(server.TransportType)
            ? !string.IsNullOrWhiteSpace(server.Url)
            : !string.IsNullOrWhiteSpace(server.Command);

    private static bool IsSupportedTransport(string? transportType) =>
        McpTransportKinds.IsStdio(transportType) || McpTransportKinds.IsStreamableHttp(transportType);

    private static IMcpClient CreateClient(string name, McpServerSettings server)
    {
        if (McpTransportKinds.IsStreamableHttp(server.TransportType))
        {
            return new McpStreamableHttpClient(name, server.Url, server.Headers);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = server.Command,
            Arguments = server.Args.Count == 0 ? string.Empty : string.Join(" ", server.Args),
            WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (var (key, value) in server.Env)
        {
            startInfo.Environment[key] = value;
        }

        return new McpStdioClient(name, startInfo);
    }

    public async ValueTask DisposeAsync()
    {
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

