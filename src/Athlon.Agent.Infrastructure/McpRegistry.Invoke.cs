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

/// <summary>Tool invocation and argument normalization.</summary>
public sealed partial class McpRegistry
{
    public async Task<ToolResult> InvokeAsync(string serverName, string toolName, ToolCallArguments args, CancellationToken cancellationToken = default)
    {
        if (!ScheduleTurnScope.IsMcpServerAllowed(serverName))
        {
            return ToolResult.Failure(
                "Tool not available",
                $"MCP server '{serverName}' is not allowed for this scheduled turn.");
        }

        var catalogTool = _tools.TryGetValue(serverName, out var serverTools)
            ? serverTools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (catalogTool is null)
        {
            return ToolInvocationErrors.Failure(
                "MCP tool schema unavailable",
                new ToolInvocationError(
                    "mcp.schema_not_found",
                    "$",
                    "a tool present in the current MCP catalog",
                    $"{serverName}/{toolName}",
                    "Refresh the MCP catalog and search for the tool again before calling it."));
        }

        var schemaFailure = ValidateArgumentsAgainstSchema(catalogTool.InputSchemaJson, args);
        if (schemaFailure is not null)
        {
            return schemaFailure;
        }

        if (!_clients.TryGetValue(serverName, out var client))
        {
            return ToolResult.Failure("MCP server not available", $"Server '{serverName}' is not enabled or not connected.");
        }

        try
        {
            var argumentsJson = args.ToJsonString();
            argumentsJson = NormalizeMcpArgumentsJson(serverName, toolName, argumentsJson, workspaceContext.RootPath);

            using var timeoutCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(_toolCallTimeoutSeconds.GetValueOrDefault(serverName, 120)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var resultJson = await client.CallToolAsync(toolName, argumentsJson, linkedCts.Token);
            _statuses[serverName] = client.Status;
            if (McpResultIsError(resultJson))
            {
                return ToolResult.Failure($"MCP tool {toolName} failed.", resultJson);
            }

            return ToolResult.Success($"MCP tool {toolName} returned.", resultJson);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _statuses[serverName] = client.Status with
            {
                State = McpConnectionState.Error,
                LastError = $"Tool call timed out after {_toolCallTimeoutSeconds.GetValueOrDefault(serverName, 120)}s."
            };
            return ToolResult.Failure("MCP tool call timed out", _statuses[serverName].LastError ?? "MCP tool call timed out.");
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
        McpTransportKinds.IsHttp(server.TransportType)
            ? !string.IsNullOrWhiteSpace(server.Url)
            : !string.IsNullOrWhiteSpace(server.Command);

    internal static ToolResult? ValidateArgumentsAgainstSchema(
        string inputSchemaJson,
        ToolCallArguments arguments)
    {
        try
        {
            var validationError = ToolInvocationValidator.Validate(
                ToolSchema.FromMcp(inputSchemaJson),
                arguments);
            return validationError is null
                ? null
                : ToolInvocationErrors.Failure("Invalid MCP tool arguments", validationError);
        }
        catch (JsonException ex)
        {
            return ToolInvocationErrors.Failure(
                "MCP tool schema invalid",
                new ToolInvocationError(
                    "mcp.schema_invalid",
                    "$",
                    "valid JSON Schema from the MCP catalog",
                    ex.Message,
                    "Refresh or fix the MCP server schema before retrying the call."));
        }
    }

    private static bool IsSupportedTransport(string? transportType) =>
        McpTransportKinds.IsStdio(transportType) || McpTransportKinds.IsHttp(transportType);

    private static string CreateConfigFingerprint(McpServerSettings server) =>
        JsonSerializer.Serialize(server, JsonFileStore.Options);

    /// <summary>
    /// Config fingerprint combined with the workspace root the server connects with. stdio
    /// servers are started with the workspace root as cwd, so a workspace/session switch with
    /// an otherwise identical config must still reconnect; this composite drives that decision.
    /// </summary>
    private static string CreateEffectiveFingerprint(McpServerSettings server, string? workspaceRoot) =>
        CreateConfigFingerprint(server) + "\n" + (workspaceRoot ?? "<no-workspace>");

    /// <summary>
    /// Inject SSO <c>MCP_REFRESH_TOKEN</c> into stdio MCP process env (same as <see cref="WindowsCmdEncoding"/>).
    /// Fingerprint stays on the original settings so token rotation alone does not force reconnect.
    /// </summary>
    internal static McpServerSettings WithStdioSsoEnvironment(McpServerSettings server)
    {
        if (McpTransportKinds.IsHttp(server.TransportType))
        {
            return server;
        }

        var env = new Dictionary<string, string>(server.Env, StringComparer.Ordinal);
        SsoEenoEnvironment.TryApply(env);
        if (!env.ContainsKey(SsoEenoEnvironment.EnvVarName))
        {
            return server;
        }

        return new McpServerSettings
        {
            Name = server.Name,
            Enabled = server.Enabled,
            TransportType = server.TransportType,
            Url = server.Url,
            Command = server.Command,
            Args = server.Args.ToList(),
            Env = env,
            Headers = new Dictionary<string, string>(server.Headers),
            WorkingDirectory = server.WorkingDirectory,
            ToolCallTimeoutSeconds = server.ToolCallTimeoutSeconds
        };
    }

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

    private static string ResolveTransportLabel(McpServerSettings server)
    {
        if (!McpTransportKinds.IsHttp(server.TransportType))
        {
            return "stdio";
        }

        var mode = McpTransportKinds.ResolveHttpTransportMode(server.TransportType, server.Url);
        return McpTransportKinds.FormatHttpTransportLabel(mode);
    }
}
