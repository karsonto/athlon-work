using Athlon.Agent.Core;
using Athlon.Agent.Core.Tools;

namespace Athlon.Agent.Infrastructure.Tools;

/// <summary>One contributor to the tool catalog (local, MCP catalog, or search gateway).</summary>
public interface IToolSource
{
    string Name { get; }

    /// <summary>Whether this source should contribute definitions for the current routing snapshot.</summary>
    bool IsActive(ToolAvailabilityContext availability, bool preferMcpSearch, bool mcpBlocked);

    IReadOnlyList<ToolDefinition> ListDefinitions(ToolAvailabilityContext availability);

    ToolDefinition? FindDefinition(string name, ToolAvailabilityContext availability);

    bool IsParallelizable(string toolName, ToolAvailabilityContext availability);

    bool Owns(string toolName);

    Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default);
}
