namespace Athlon.Agent.Core;

public static class ParallelToolPolicy
{
    private static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_read",
        "grep_files",
        "glob_files",
        "file_list",
        "memory_search"
    };

    public static bool IsParallelizable(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && Allowlist.Contains(toolName);

    public static bool CanParallelizeBatch(
        IReadOnlyList<AgentToolCall> calls,
        ParallelToolExecutionSettings settings) =>
        settings.Enabled
        && calls.Count > 1
        && calls.All(call => IsParallelizable(call.Name));

    public static IReadOnlySet<string> AllowedToolNames => Allowlist;
}
