using System.Text.Json;
using ModelContextProtocol.Client;
namespace Athlon.Agent.Mcp;

/// <summary>
/// <see cref="IMcpClient"/> adapter over the official Model Context Protocol C# SDK.
/// </summary>
public sealed class SdkMcpClient : IMcpClient
{
    private readonly ModelContextProtocol.Client.McpClient _client;
    private readonly string _transport;
    private readonly Func<string?>? _getLastStderrLine;

    private McpConnectionState _state = McpConnectionState.Connected;
    private List<McpTool> _tools = new();
    private string? _lastError;

    internal SdkMcpClient(
        string name,
        string transport,
        ModelContextProtocol.Client.McpClient client,
        Func<string?>? getLastStderrLine)
    {
        Name = name;
        _transport = transport;
        _client = client;
        _getLastStderrLine = getLastStderrLine;
    }

    public string Name { get; }

    public McpServerStatus Status => new(
        Name,
        _state,
        Transport: _transport,
        Tools: _tools.ToArray(),
        LastError: _lastError ?? _getLastStderrLine?.Invoke());

    public Task InitializeAsync(string? clientName = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            _tools = tools.Select(MapTool).ToList();
            _state = McpConnectionState.Connected;
            _lastError = null;
            return _tools;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state = McpConnectionState.Error;
            _lastError = ex.Message;
            throw;
        }
    }

    public async Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _client.CallToolAsync(
                toolName,
                McpArgumentsJson.ParseDictionary(argumentsJson),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            _state = McpConnectionState.Connected;
            _lastError = null;
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state = McpConnectionState.Error;
            _lastError = ex.Message;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private static McpTool MapTool(McpClientTool tool) =>
        new(
            tool.Name,
            tool.Description ?? string.Empty,
            tool.JsonSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "{}"
                : tool.JsonSchema.GetRawText());
}
