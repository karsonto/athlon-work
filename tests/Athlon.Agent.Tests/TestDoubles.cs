using Athlon.Agent.Core;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests;

internal sealed class NoOpActiveAgentSessionContext : IActiveAgentSessionContext
{
    private static readonly AsyncLocal<string?> AmbientSessionId = new();

    public string? SessionId => AmbientSessionId.Value;

    public void SetSession(string? sessionId) => AmbientSessionId.Value = sessionId;

    public IDisposable Enter(string sessionId)
    {
        var previous = AmbientSessionId.Value;
        AmbientSessionId.Value = sessionId;
        return new SessionScope(previous);
    }

    private sealed class SessionScope(string? previous) : IDisposable
    {
        public void Dispose() => AmbientSessionId.Value = previous;
    }
}

internal sealed class NoOpPlanNotebook : IPlanNotebook
{
    public AgentPlan? GetCurrent(string sessionId) => null;

    public PlanOperationResult CreatePlan(string sessionId, CreatePlanRequest request) =>
        new(false, "No plan notebook in test.");

    public PlanOperationResult FinishSubtask(string sessionId, int subtaskIndex, string outcome) =>
        new(false, "No plan notebook in test.");

    public string GetPlanMarkdown(string sessionId, bool detailed = true) => string.Empty;

    public void Clear(string sessionId)
    {
    }
}
