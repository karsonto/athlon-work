using Athlon.Agent.Core.Browser;
using Athlon.Agent.Core.ComputerUse;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.Terminal;

namespace Athlon.Agent.Core.Tools;

/// <summary>Maps an <see cref="IAgentTool"/> to <see cref="ToolFacet"/> flags.</summary>
public static class ToolFacetClassifier
{
    private static readonly HashSet<string> WriteFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_write",
        "file_edit",
        "apply_patch"
    };

    private static readonly HashSet<string> ShellToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "execute_command"
    };

    public static ToolFacet Classify(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var facets = ToolFacet.None;
        var name = tool.Definition.Name;

        if (tool is IComputerUseTool)
        {
            facets |= ToolFacet.ComputerUse;
        }

        if (tool is ILocalWorkspaceTool)
        {
            facets |= ToolFacet.LocalWorkspace;
        }

        if (tool is IRemoteWorkspaceTool)
        {
            facets |= ToolFacet.RemoteWorkspace;
        }

        if (tool is IBrowserTool)
        {
            facets |= ToolFacet.Browser;
        }

        if (tool is ITerminalTool)
        {
            facets |= ToolFacet.Terminal;
        }

        if (tool is IHarnessTool)
        {
            facets |= ToolFacet.HarnessTodo;
        }

        if (tool is Athlon.Agent.Core.Plan.IPlanDocumentTool)
        {
            facets |= ToolFacet.PlanDocument;
        }

        if (tool is Athlon.Agent.Core.Plan.IPlanClarifyTool)
        {
            facets |= ToolFacet.PlanClarify;
        }

        if (tool is ISubAgentTool)
        {
            facets |= ToolFacet.SubAgent;
        }

        if (tool is ILongTermMemoryTool)
        {
            facets |= ToolFacet.Memory;
        }

        if (tool is IGlobalKnowledgeTool)
        {
            facets |= ToolFacet.Knowledge;
        }

        if (string.Equals(name, "browser_navigate", StringComparison.OrdinalIgnoreCase))
        {
            facets |= ToolFacet.BrowserBootstrap;
        }

        if (string.Equals(name, "terminal_open", StringComparison.OrdinalIgnoreCase))
        {
            facets |= ToolFacet.TerminalBootstrap;
        }

        if (WriteFileNames.Contains(name))
        {
            facets |= ToolFacet.WriteFileOrShell;
        }

        if (ShellToolNames.Contains(name))
        {
            facets |= ToolFacet.Shell;
        }

        return facets;
    }
}
