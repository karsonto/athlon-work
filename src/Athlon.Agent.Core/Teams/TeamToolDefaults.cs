namespace Athlon.Agent.Core.Teams;

public static class TeamToolDefaults
{
    public static readonly IReadOnlyList<string> WorkerSafeTools =
    [
        "file_list",
        "file_read",
        "file_write",
        "file_edit",
        "apply_patch",
        "grep_files",
        "glob_files",
        "knowledge_search",
        "load_skill_through_path"
    ];

    public static readonly IReadOnlyList<string> LeaderExtraTools =
    [
        "team_list_members",
        "team_create_subtask",
        "team_dispatch",
        "team_redispatch",
        "team_await",
        "team_collect",
        "team_submit_result",
        "team_ask_user"
    ];

    public static IReadOnlyList<string> ResolveAllowList(AgentProfile profile, bool isLeader)
    {
        if (profile.ToolAllowList is { Count: > 0 })
        {
            return profile.ToolAllowList;
        }

        if (isLeader)
        {
            return WorkerSafeTools.Concat(LeaderExtraTools).ToArray();
        }

        return WorkerSafeTools;
    }

    public static bool MatchesAllowList(string toolName, IReadOnlyList<string> allowList)
    {
        foreach (var entry in allowList)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (entry.EndsWith('*'))
            {
                var prefix = entry[..^1];
                if (toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(entry, toolName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
