namespace Athlon.Agent.Core;

public readonly record struct ToolCallGroup(bool Parallel, int Start, int Count);

public static class ParallelToolPolicy
{
    public static bool CanParallelizeBatch(
        IReadOnlyList<AgentToolCall> calls,
        ParallelToolExecutionSettings settings,
        IToolRouter router) =>
        settings.Enabled
        && calls.Count > 1
        && calls.All(call => router.IsParallelizable(call.Name));

    /// <summary>
    /// Splits a model tool batch into execution groups. Two or more consecutive parallelizable
    /// calls run together; every other call stays in its own serial group. Order is preserved.
    /// </summary>
    public static IReadOnlyList<ToolCallGroup> Partition(
        IReadOnlyList<AgentToolCall> calls,
        ParallelToolExecutionSettings settings,
        IToolRouter router)
    {
        if (calls.Count == 0)
        {
            return [];
        }

        if (!settings.Enabled)
        {
            return calls.Select((_, index) => new ToolCallGroup(false, index, 1)).ToArray();
        }

        var groups = new List<ToolCallGroup>();
        var index = 0;
        while (index < calls.Count)
        {
            if (!router.IsParallelizable(calls[index].Name))
            {
                groups.Add(new ToolCallGroup(false, index, 1));
                index++;
                continue;
            }

            var start = index;
            while (index < calls.Count && router.IsParallelizable(calls[index].Name))
            {
                index++;
            }

            var count = index - start;
            groups.Add(new ToolCallGroup(count > 1, start, count));
        }

        return groups;
    }
}
