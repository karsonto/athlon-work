using System.Collections.Concurrent;
using Athlon.Agent.Core.Plan;

namespace Athlon.Agent.Tests;

internal sealed class StubPlanNotebook : IPlanNotebook
{
    private readonly ConcurrentDictionary<string, AgentPlan> _plans = new(StringComparer.Ordinal);

    public void SetPlan(string sessionId, AgentPlan plan) => _plans[sessionId] = plan;

    public AgentPlan? GetCurrent(string sessionId) =>
        _plans.TryGetValue(sessionId, out var plan) ? plan : null;

    public PlanOperationResult CreatePlan(string sessionId, CreatePlanRequest request) =>
        new(false, "Stub only.");

    public PlanOperationResult ApprovePlan(string sessionId) =>
        new(false, "Stub only.");

    public PlanOperationResult FinishSubtask(string sessionId, int subtaskIndex, string outcome) =>
        new(false, "Stub only.");

    public string GetPlanMarkdown(string sessionId, bool detailed = true) =>
        GetCurrent(sessionId) is { } plan ? PlanMarkdownFormatter.ToMarkdown(plan, detailed) : string.Empty;

    public string? TryGetPlanFilePath() => null;

    public void Clear(string sessionId) => _plans.TryRemove(sessionId, out _);
}
