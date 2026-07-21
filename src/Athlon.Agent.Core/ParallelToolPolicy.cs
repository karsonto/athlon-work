namespace Athlon.Agent.Core;

public static class ParallelToolPolicy
{
    public static bool CanParallelizeBatch(
        IReadOnlyList<AgentToolCall> calls,
        ParallelToolExecutionSettings settings,
        IToolRouter router) =>
        settings.Enabled
        && calls.Count > 1
        && calls.All(call => router.IsParallelizable(call.Name));
}
