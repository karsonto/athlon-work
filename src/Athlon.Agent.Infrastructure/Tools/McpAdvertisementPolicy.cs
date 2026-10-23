using System.Collections.Concurrent;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Core.Tools;

namespace Athlon.Agent.Infrastructure.Tools;

/// <summary>
/// Decides whether MCP tools (direct catalog or search gateway) may be advertised / invoked.
/// </summary>
public sealed class McpAdvertisementPolicy(
    AppSettings settings,
    IMcpRegistry mcpRegistry,
    IActiveAgentSessionContext activeSessionContext)
{
    private readonly ConcurrentDictionary<string, bool> _autoSearchStickyBySession = new(StringComparer.Ordinal);

    public bool IsMcpBlocked(ToolAvailabilityContext context)
    {
        if (context.ComputerUseActive || !context.HasWorkspace)
        {
            return true;
        }

        if (context.Mode == SessionAgentMode.Ask)
        {
            return true;
        }

        if (context.Mode == SessionAgentMode.Debug)
        {
            var phase = context.ActiveDebugPhase;
            return phase is null || phase.Value.BlocksMcp();
        }

        if (context.Mode == SessionAgentMode.Plan)
        {
            var phase = context.ActivePlanPhase;
            return phase is null || phase.Value.BlocksMcp();
        }

        return false;
    }

    public string? McpBlockedReason(ToolAvailabilityContext context)
    {
        if (context.ComputerUseActive)
        {
            return "Tool is not available during a Computer Use turn.";
        }

        if (!context.HasWorkspace)
        {
            return "This tool is not available without a configured workspace.";
        }

        if (context.Mode == SessionAgentMode.Ask)
        {
            return "MCP tools are not available in Ask mode. Use read-only workspace tools only.";
        }

        if (context.Mode == SessionAgentMode.Debug)
        {
            return "MCP tools are not available during this Debug phase. Use file/grep tools, then diagnose_logs after the user reproduces.";
        }

        if (context.Mode == SessionAgentMode.Plan)
        {
            return "MCP tools are not available while waiting for plan confirmation. Answer clarifying questions only.";
        }

        return null;
    }

    public bool ShouldUseSearch()
    {
        var config = settings.McpSearch;
        if (!config.Enabled)
        {
            ClearStickyForActiveSession();
            return false;
        }

        if (string.Equals(config.Mode, "direct", StringComparison.OrdinalIgnoreCase))
        {
            ClearStickyForActiveSession();
            return false;
        }

        if (string.Equals(config.Mode, "search", StringComparison.OrdinalIgnoreCase))
        {
            ClearStickyForActiveSession();
            return mcpRegistry.CatalogCount > 0;
        }

        // auto
        var enterSearch = MeetsAutoEnterThreshold(config);
        var sessionId = activeSessionContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return enterSearch;
        }

        if (_autoSearchStickyBySession.TryGetValue(sessionId, out var stuckSearch))
        {
            if (stuckSearch)
            {
                if (MeetsAutoExitThreshold(config))
                {
                    _autoSearchStickyBySession[sessionId] = false;
                    return false;
                }

                return true;
            }

            if (enterSearch)
            {
                _autoSearchStickyBySession[sessionId] = true;
                return true;
            }

            return false;
        }

        _autoSearchStickyBySession[sessionId] = enterSearch;
        return enterSearch;
    }

    private bool MeetsAutoEnterThreshold(McpSearchSettings config) =>
        mcpRegistry.CatalogCount >= config.AutoThresholdToolCount
        || mcpRegistry.CatalogSchemaCharCount >= config.AutoThresholdSchemaChars;

    private bool MeetsAutoExitThreshold(McpSearchSettings config)
    {
        var exitToolCount = Math.Max(0, config.AutoThresholdToolCount - Math.Max(0, config.AutoHysteresisToolCount));
        var exitSchemaChars = Math.Max(0, config.AutoThresholdSchemaChars - Math.Max(0, config.AutoHysteresisSchemaChars));
        return mcpRegistry.CatalogCount < exitToolCount
            && mcpRegistry.CatalogSchemaCharCount < exitSchemaChars;
    }

    private void ClearStickyForActiveSession()
    {
        var sessionId = activeSessionContext.SessionId;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _autoSearchStickyBySession.TryRemove(sessionId, out _);
        }
    }
}
