using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Core.Tools;

namespace Athlon.Agent.Infrastructure.Tools;

/// <summary>Direct MCP catalog advertisement (non-search mode).</summary>
public sealed class McpCatalogToolSource(
    IMcpRegistry mcpRegistry,
    IAgentRunContextAccessor runContextAccessor,
    IActiveAgentSessionContext activeSessionContext,
    IRuntimeDiagnosticEventSink? runtimeDiagnosticEventSink = null) : IToolSource
{
    public string Name => "mcp-catalog";

    public bool IsActive(ToolAvailabilityContext availability, bool preferMcpSearch, bool mcpBlocked) =>
        !mcpBlocked && !preferMcpSearch;

    public bool Owns(string toolName) =>
        McpToolNameCodec.TryDecode(toolName, out _, out _);

    public IReadOnlyList<ToolDefinition> ListDefinitions(ToolAvailabilityContext availability) =>
        mcpRegistry.ListToolDefinitions();

    public ToolDefinition? FindDefinition(string name, ToolAvailabilityContext availability) =>
        mcpRegistry.ListToolDefinitions()
            .FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool IsParallelizable(string toolName, ToolAvailabilityContext availability) => false;

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!McpToolNameCodec.TryDecode(invocation.ToolName, out var serverName, out var toolName))
        {
            return ToolResult.Failure("Not an MCP tool", $"Tool '{invocation.ToolName}' is not an MCP catalog tool.");
        }

        if (!ScheduleTurnScope.IsMcpServerAllowed(serverName))
        {
            return ToolResult.Failure(
                "Tool not available",
                $"MCP server '{serverName}' is not allowed for this scheduled turn.");
        }

        if (!invocation.SkipValidation)
        {
            var mcpDefinition = FindDefinition(invocation.ToolName, availability: null!);
            if (mcpDefinition is not null)
            {
                var validationError = ToolInvocationValidator.Validate(mcpDefinition.ParametersSchema, invocation.Arguments);
                if (validationError is not null)
                {
                    return ToolInvocationErrors.Failure("Invalid tool arguments", validationError);
                }

                var blocked = ToolInvocationPolicyEnforcer.TryBlockInvocation(
                    mcpDefinition,
                    invocation.ApprovalDecision);
                if (blocked is not null)
                {
                    return blocked;
                }
            }
        }

        try
        {
            return await mcpRegistry.InvokeAsync(serverName, toolName, invocation.Arguments, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (runtimeDiagnosticEventSink is { } sink)
            {
                var context = runContextAccessor.Current;
                var evt = new RuntimeDiagnosticEvent(
                    eventId: "",
                    ts: default,
                    sequence: 0,
                    sessionId: context?.SessionId ?? activeSessionContext.SessionId,
                    runId: context?.RunId ?? activeSessionContext.SessionId,
                    turnId: null,
                    attemptId: null,
                    parentAttemptId: null,
                    toolCallId: null,
                    messageId: null,
                    component: RuntimeDiagnosticComponent.Mcp,
                    phase: RuntimeDiagnosticPhase.Invoke,
                    eventType: "mcp.tool_invoke_failed",
                    severity: RuntimeDiagnosticSeverity.Error,
                    errorCode: RuntimeDiagnosticErrorCodes.McpToolInvokeFailed,
                    message: $"{serverName}.{toolName}: {ex.Message}");
                await sink.EnqueueAsync(evt, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }
}
