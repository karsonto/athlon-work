using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class WorkspaceIgnoreResolverTests
{
    [Fact]
    public void Resolve_UsesGlobalWhenWorkspaceEmpty()
    {
        var result = WorkspaceIgnoreResolver.Resolve(
            workspacePatterns: [],
            globalPatterns: ["dist", "node_modules"]);

        Assert.Equal(["dist", "node_modules"], result);
    }

    [Fact]
    public void Resolve_WorkspaceOverridesGlobal()
    {
        var result = WorkspaceIgnoreResolver.Resolve(
            workspacePatterns: [".git"],
            globalPatterns: ["dist", "node_modules"]);

        Assert.Equal([".git"], result);
    }

    [Fact]
    public void Resolve_FallsBackToBuiltInWhenAllEmpty()
    {
        var result = WorkspaceIgnoreResolver.Resolve(workspacePatterns: [], globalPatterns: []);

        Assert.Contains("node_modules", result);
        Assert.Contains("dist", result);
    }
}
