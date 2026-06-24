using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Tests;

public sealed class AgentRunContextTests
{
    [Fact]
    public void CreateRoot_UsesSessionIdAndWorkspace()
    {
        var session = AgentSession.Create("test") with { Id = "sess-1", ActiveWorkspace = @"C:\work" };
        var context = AgentRunContext.CreateRoot(
            session,
            "run-1",
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            WorkspaceIgnoreDefaults.BuiltIn);

        Assert.Equal("sess-1", context.SessionId);
        Assert.Equal("run-1", context.RunId);
        Assert.Equal(AgentRunKind.Root, context.Kind);
        Assert.Null(context.ParentSessionId);
        Assert.Equal(Path.GetFullPath(@"C:\work"), context.WorkspaceRoot);
    }

    [Fact]
    public void CreateChild_ResolvesSubAgentDirectory()
    {
        var root = AgentRunContext.CreateRoot(
            AgentSession.Create("parent") with { Id = "parent-1" },
            "run-root",
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            WorkspaceIgnoreDefaults.BuiltIn);

        var child = root.CreateChild(
            "sub-1",
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            "Searcher",
            new AgentLoopOptions { MaxModelToolRounds = 2 },
            null,
            WorkspaceIgnoreDefaults.BuiltIn);

        Assert.Equal(AgentRunKind.SubAgent, child.Kind);
        Assert.Equal("parent-1", child.ParentSessionId);
        Assert.Equal("Searcher", child.SubAgentRole);
        Assert.Equal(2, child.LoopOptions?.MaxModelToolRounds);

        var dir = child.ResolveSessionDirectory(@"C:\sessions", "sub-1");
        Assert.Equal(
            Path.Combine(@"C:\sessions", "parent-1", "subagents", "default", "sub-1"),
            dir);
    }

    [Fact]
    public void Accessor_PushAndDispose_RestoresPrevious()
    {
        var accessor = new AgentRunContextAccessor();
        var root = AgentRunContext.CreateRoot(
            AgentSession.Create("parent") with { Id = "parent" },
            "run-a",
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            WorkspaceIgnoreDefaults.BuiltIn);
        var child = root.CreateChild(
            "sub",
            new NoOpToolRouter(),
            PromptTestHelpers.CreateStaticOrchestrator(),
            "role",
            null,
            null,
            WorkspaceIgnoreDefaults.BuiltIn);

        using (accessor.Push(root))
        {
            Assert.Equal("parent", accessor.Current?.SessionId);
            using (accessor.Push(child))
            {
                Assert.Equal("sub", accessor.Current?.SessionId);
            }

            Assert.Equal("parent", accessor.Current?.SessionId);
        }

        Assert.Null(accessor.Current);
    }

    [Fact]
    public void IsSubAgentSessionPath_DetectsSubAgentLayout()
    {
        var path = Path.Combine("sessions", "parent", "subagents", "default", "sub", "session.json");
        Assert.True(AgentRunContext.IsSubAgentSessionPath(path));
        Assert.False(AgentRunContext.IsSubAgentSessionPath(Path.Combine("sessions", "parent", "session.json")));
    }

    [Fact]
    public void ResolveSessionDirectory_FallsBackWithoutContext()
    {
        var accessor = new AgentRunContextAccessor();
        Assert.Equal(
            Path.Combine(@"C:\sessions", "plain"),
            accessor.ResolveSessionDirectory(@"C:\sessions", "plain"));
    }
}
