using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Middleware;
using Athlon.Agent.Core.Streaming;

namespace Athlon.Agent.Tests;

public sealed class AgentRunStateTests
{
    [Fact]
    public void TurnInvocation_ExposesMutableRunState()
    {
        var invocation = new AgentTurnInvocation
        {
            RunContext = AgentRunContext.CreateRoot(
                AgentSession.Create("s") with { Id = "s" },
                "run",
                new NoOpToolRouter(),
                PromptTestHelpers.CreateStaticOrchestrator(),
                WorkspaceIgnoreDefaults.BuiltIn),
            Session = AgentSession.Create("s") with { Id = "s" },
            StreamAdapter = new Athlon.Agent.Core.Streaming.AgentStreamAdapter("s", "run")
        };

        invocation.State.ModelToolRound = 2;
        invocation.State.ToolStorm = new ToolStormBreaker(new ToolStormSettings());
        invocation.State.PendingApproval = new PendingToolApproval(
            "c1",
            "execute_command",
            ToolCallArguments.Empty,
            ToolInvocationPolicy.Ask,
            DateTimeOffset.UtcNow);

        Assert.Equal(2, invocation.State.ModelToolRound);
        Assert.NotNull(invocation.State.ToolStorm);
        Assert.Equal("c1", invocation.State.PendingApproval?.ToolCallId);
    }
}
