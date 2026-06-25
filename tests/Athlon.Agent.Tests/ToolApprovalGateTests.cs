using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Tests;

public sealed class ToolApprovalGateTests
{
    [Fact]
    public void IsApprovalRequired_RespectsGlobalSwitch()
    {
        var settings = new AppSettings { ToolPermissions = { RequireToolApproval = false } };
        var definition = new ToolDefinition(
            "file_write",
            "write",
            new Dictionary<string, string>(),
            RequiresApproval: true);
        var call = new AgentToolCall("c1", "file_write", new Dictionary<string, string>());

        Assert.False(ToolApprovalGate.IsApprovalRequired(settings, null, call, definition));

        settings.ToolPermissions.RequireToolApproval = true;
        Assert.True(ToolApprovalGate.IsApprovalRequired(settings, null, call, definition));
    }

    [Fact]
    public void IsApprovalRequired_RunContextOverridesGlobal()
    {
        var settings = new AppSettings { ToolPermissions = { RequireToolApproval = true } };
        var definition = new ToolDefinition(
            "execute_command",
            "cmd",
            new Dictionary<string, string>(),
            RequiresApproval: true,
            InvocationPolicy: ToolInvocationPolicy.Ask);
        var call = new AgentToolCall("c1", "execute_command", new Dictionary<string, string>());
        var runContext = new AgentRunContext
        {
            SessionId = "s1",
            RunId = "r1",
            ToolRouter = new StubToolRouter(),
            PromptOrchestrator = new StubPromptOrchestrator(),
            RequireToolApproval = false
        };

        Assert.False(ToolApprovalGate.IsApprovalRequired(settings, runContext, call, definition));
    }

    private sealed class StubToolRouter : IToolRouter
    {
        public IReadOnlyList<ToolDefinition> ListTools() => Array.Empty<ToolDefinition>();
        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    private sealed class StubPromptOrchestrator : ISystemPromptOrchestrator
    {
        public FrozenSystemPrompt PrepareForTurn(AgentSession session, IReadOnlyList<ToolDefinition> tools) =>
            new("prompt");
        public string BuildForReasoningIteration(FrozenSystemPrompt frozen, AgentSession session, IReadOnlyList<ToolDefinition> tools) =>
            frozen.Text;
    }
}
