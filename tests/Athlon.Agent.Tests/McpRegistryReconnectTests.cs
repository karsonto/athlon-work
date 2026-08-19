using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

public sealed class McpRegistryReconnectTests
{
    [Fact]
    public async Task ReconnectAsync_DoesNothing_WhenServerMissingOrDisabled()
    {
        await using var registry = new McpRegistry(
            new NoOpLogger(),
            new FixedWorkspaceContext(null),
            new AgentRunContextAccessor(),
            new NullRuntimeDiagnosticEventSink());

        await registry.ReconnectAsync("missing", Array.Empty<McpServerSettings>());
        await registry.ReconnectAsync(
            "demo",
            [
                new McpServerSettings
                {
                    Name = "demo",
                    Enabled = false,
                    Command = "echo"
                }
            ]);

        Assert.Empty(registry.GetStatuses());
    }

    [Fact]
    public async Task TestMcpRegistry_TracksReconnectCalls()
    {
        var registry = new TestMcpRegistry();
        await registry.ReconnectAsync(
            "demo",
            [new McpServerSettings { Name = "demo", Enabled = true, Command = "echo" }]);

        Assert.Equal(1, registry.ReconnectCount);
        Assert.Equal("demo", registry.LastReconnectServerName);
    }

    private sealed class FixedWorkspaceContext(string? rootPath) : IActiveWorkspaceContext
    {
        public string? RootPath { get; private set; } = rootPath;
        public string? DisplayName { get; private set; }
        public IReadOnlyList<string> IgnorePatterns { get; private set; } = Array.Empty<string>();
        public WorkspaceKind Kind { get; private set; } = WorkspaceKind.Local;
        public string? WorkspaceId { get; private set; }

        public void SetWorkspace(string? path, string? displayName = null, IReadOnlyList<string>? ignorePatterns = null)
        {
            RootPath = path;
            DisplayName = displayName;
            IgnorePatterns = ignorePatterns ?? Array.Empty<string>();
        }

        public void SetWorkspace(
            string? path,
            WorkspaceKind kind,
            string? workspaceId,
            string? displayName = null,
            IReadOnlyList<string>? ignorePatterns = null)
        {
            RootPath = path;
            Kind = kind;
            WorkspaceId = workspaceId;
            DisplayName = displayName;
            IgnorePatterns = ignorePatterns ?? Array.Empty<string>();
        }
    }
}
