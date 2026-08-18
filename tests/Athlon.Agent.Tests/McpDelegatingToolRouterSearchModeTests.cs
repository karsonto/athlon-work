using Athlon.Agent.Core;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class McpDelegatingToolRouterSearchModeTests
{
    [Theory]
    [InlineData("direct", 20, false)]
    [InlineData("search", 1, true)]
    [InlineData("auto", 1, false)]
    [InlineData("auto", 20, true)]
    public void ListTools_RespectsDirectSearchAndAutoModes(
        string mode,
        int toolCount,
        bool expectsSearchGateway)
    {
        var registry = new TestMcpRegistry(CreateCatalog(toolCount));
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings
            {
                Enabled = true,
                Mode = mode,
                AutoThresholdToolCount = 12,
                AutoThresholdSchemaChars = int.MaxValue
            }
        };

        var tools = CreateRouter(registry, settings).ListTools();

        Assert.Equal(
            expectsSearchGateway,
            tools.Any(tool => tool.Name == McpSearchGatewayTools.SearchToolName));
        Assert.Equal(
            !expectsSearchGateway,
            tools.Any(tool => tool.Name.StartsWith("mcp_server__", StringComparison.Ordinal)));
    }

    [Fact]
    public void ListTools_uses_gateway_tools_when_threshold_exceeded()
    {
        var catalog = CreateCatalog(15);

        var registry = new TestMcpRegistry(catalog);
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings { Enabled = true, Mode = "auto", AutoThresholdToolCount = 12 }
        };

        var router = CreateRouter(registry, settings);

        var tools = router.ListTools();

        Assert.Contains(tools, tool => tool.Name == McpSearchGatewayTools.SearchToolName);
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("mcp_server__tool_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("search", true)]
    public async Task InvokeAsync_GatewayAvailabilityMatchesMode(string mode, bool expectedSuccess)
    {
        var registry = new TestMcpRegistry(CreateCatalog(1));
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings
            {
                Enabled = true,
                Mode = mode,
                MinScore = 0.01
            }
        };
        var router = CreateRouter(registry, settings);

        var result = await router.InvokeAsync(new ToolInvocation(
            McpSearchGatewayTools.SearchToolName,
            ToolCallArgumentsParser.ParseJson("""{"query":"tool"}""")));

        Assert.Equal(expectedSuccess, result.Succeeded);
    }

    [Fact]
    public void ListTools_AutoMode_StickySearch_UsesHysteresisBeforeExit()
    {
        var registry = new TestMcpRegistry(CreateCatalog(15));
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings
            {
                Enabled = true,
                Mode = "auto",
                AutoThresholdToolCount = 12,
                AutoThresholdSchemaChars = int.MaxValue,
                AutoHysteresisToolCount = 3,
                AutoHysteresisSchemaChars = 0
            }
        };
        var router = CreateRouter(registry, settings);

        Assert.Contains(router.ListTools(), tool => tool.Name == McpSearchGatewayTools.SearchToolName);

        // Still above exit band (12 - 3 = 9): stay in search.
        registry.SetCatalog(CreateCatalog(10));
        Assert.Contains(router.ListTools(), tool => tool.Name == McpSearchGatewayTools.SearchToolName);

        // Below exit band: leave search.
        registry.SetCatalog(CreateCatalog(8));
        Assert.DoesNotContain(router.ListTools(), tool => tool.Name == McpSearchGatewayTools.SearchToolName);
        Assert.Contains(router.ListTools(), tool => tool.Name.StartsWith("mcp_server__", StringComparison.Ordinal));
    }

    [Fact]
    public void ListTools_DirectMode_IgnoresStickySearch()
    {
        var registry = new TestMcpRegistry(CreateCatalog(15));
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings
            {
                Enabled = true,
                Mode = "auto",
                AutoThresholdToolCount = 12,
                AutoThresholdSchemaChars = int.MaxValue
            }
        };
        var router = CreateRouter(registry, settings);
        Assert.Contains(router.ListTools(), tool => tool.Name == McpSearchGatewayTools.SearchToolName);

        settings.McpSearch.Mode = "direct";
        Assert.DoesNotContain(router.ListTools(), tool => tool.Name == McpSearchGatewayTools.SearchToolName);
        Assert.Contains(router.ListTools(), tool => tool.Name.StartsWith("mcp_server__", StringComparison.Ordinal));
    }

    [Fact]
    public void ListTools_HidesMcp_InDebugHypothesize()
    {
        var registry = new TestMcpRegistry(CreateCatalog(3));
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings { Enabled = true, Mode = "direct" }
        };
        var phaseAccessor = new DebugPhaseAccessor();
        phaseAccessor.SetActiveRun(new DebugRun
        {
            Id = "run1",
            SessionId = "test-session",
            LogPath = Path.Combine(Path.GetTempPath(), "athlon-debug-run1.jsonl"),
            Phase = DebugPhase.Hypothesize
        });

        var names = CreateDebugRouter(registry, settings, phaseAccessor).ListTools().Select(tool => tool.Name).ToArray();
        Assert.DoesNotContain(McpSearchGatewayTools.SearchToolName, names);
        Assert.DoesNotContain(names, name => name.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvokeAsync_RejectsMcp_InDebugAnalyze()
    {
        var registry = new TestMcpRegistry(CreateCatalog(1));
        var settings = new AppSettings
        {
            McpSearch = new McpSearchSettings { Enabled = true, Mode = "direct" }
        };
        var phaseAccessor = new DebugPhaseAccessor();
        phaseAccessor.SetActiveRun(new DebugRun
        {
            Id = "run1",
            SessionId = "test-session",
            LogPath = Path.Combine(Path.GetTempPath(), "athlon-debug-run1.jsonl"),
            Phase = DebugPhase.Analyze
        });

        var result = await CreateDebugRouter(registry, settings, phaseAccessor).InvokeAsync(new ToolInvocation(
            McpToolNameCodec.Encode("server", "tool_0"),
            new Dictionary<string, string>()));
        Assert.False(result.Succeeded);
        Assert.Contains("Debug phase", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static McpDelegatingToolRouter CreateDebugRouter(
        IMcpRegistry registry,
        AppSettings settings,
        IDebugPhaseAccessor phaseAccessor) =>
        new(
            static tools => tools,
            Array.Empty<IAgentTool>(),
            registry,
            settings,
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(SessionAgentMode.Debug),
            RouterTestDependencies.CreateRunContextAccessor(SessionAgentMode.Debug),
            phaseAccessor,
            RouterTestDependencies.CreateWorkspaceGuard(),
            RouterTestDependencies.CreateBrowserWorkspaceState(),
            RouterTestDependencies.CreateTerminalWorkspaceState());

    private static McpDelegatingToolRouter CreateRouter(
        IMcpRegistry registry,
        AppSettings settings) =>
        new(
            static tools => tools,
            Array.Empty<IAgentTool>(),
            registry,
            settings,
            RouterTestDependencies.CreateSessionContext(),
            RouterTestDependencies.CreateSessionKnowledgeState(),
            RouterTestDependencies.CreateSessionHarnessState(),
            new AgentRunContextAccessor(),
            RouterTestDependencies.CreateDebugPhaseAccessor(),
            RouterTestDependencies.CreateWorkspaceGuard(),
            RouterTestDependencies.CreateBrowserWorkspaceState(),
            RouterTestDependencies.CreateTerminalWorkspaceState());

    private static McpCatalogEntry[] CreateCatalog(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new McpCatalogEntry(
                "server",
                $"tool_{index}",
                McpToolNameCodec.Encode("server", $"tool_{index}"),
                $"tool {index}",
                """{"type":"object","properties":{}}"""))
            .ToArray();
}
