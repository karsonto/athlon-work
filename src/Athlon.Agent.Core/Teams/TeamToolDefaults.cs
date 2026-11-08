namespace Athlon.Agent.Core.Teams;

public static class TeamToolDefaults
{
    /// <summary>Shared blackboard tools available to Leader and Workers.</summary>
    public static readonly IReadOnlyList<string> SharedTeamTools =
    [
        "team_progress",
        "team_memory"
    ];

    /// <summary>Auto-attached when a Worker has any Skill selected (not user-configurable).</summary>
    public const string LoadSkillThroughPathTool = "load_skill_through_path";

    /// <summary>Auto-attached when a Worker has any Knowledge module selected (not user-configurable).</summary>
    public const string KnowledgeSearchTool = "knowledge_search";

    public static readonly IReadOnlyList<string> WorkerSafeTools =
    [
        "file_list",
        "file_read",
        "file_write",
        "file_edit",
        "apply_patch",
        "grep_files",
        "glob_files"
    ];

    /// <summary>Leader-only orchestration tools (excludes shared progress/memory).</summary>
    public static readonly IReadOnlyList<string> LeaderExtraTools =
    [
        "team_list_members",
        "team_create_subtask",
        "team_dispatch",
        "team_redispatch",
        "team_collect",
        "team_submit_result",
        "team_ask_user"
    ];

    public static IReadOnlyList<string> DefaultWorkerAllowList =>
        EnsureWorkerImpliedTools(WorkerSafeTools, skillNames: null, knowledgeModuleIds: null);

    public static IReadOnlyList<string> DefaultLeaderAllowList =>
        SharedTeamTools.Concat(LeaderExtraTools).ToArray();

    public static IReadOnlyList<string> ResolveAllowList(AgentProfile profile, bool isLeader)
    {
        if (isLeader)
        {
            return profile.ToolAllowList is { Count: > 0 }
                ? profile.ToolAllowList
                : DefaultLeaderAllowList;
        }

        var baseTools = profile.ToolAllowList is { Count: > 0 }
            ? profile.ToolAllowList.Where(t => !IsLeaderOrchestrationEntry(t))
            : WorkerSafeTools;

        // Shared progress/memory always; load_skill / knowledge_search when configured.
        return EnsureWorkerImpliedTools(baseTools, profile.SkillNames, profile.KnowledgeModuleIds);
    }

    /// <summary>Force-include team_progress / team_memory for Workers (not user-configurable).</summary>
    public static IReadOnlyList<string> EnsureSharedTeamTools(IEnumerable<string> tools) =>
        EnsureWorkerImpliedTools(tools, skillNames: null, knowledgeModuleIds: null);

    /// <summary>
    /// Worker implied tools: always shared progress/memory;
    /// load_skill_through_path when Skills exist; knowledge_search when Knowledge modules exist.
    /// </summary>
    public static IReadOnlyList<string> EnsureWorkerImpliedTools(
        IEnumerable<string> tools,
        IEnumerable<string>? skillNames,
        IEnumerable<string>? knowledgeModuleIds = null)
    {
        var list = tools
            .Where(t =>
                !string.IsNullOrWhiteSpace(t)
                && !string.Equals(t, LoadSkillThroughPathTool, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(t, KnowledgeSearchTool, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var shared in SharedTeamTools)
        {
            if (!list.Contains(shared, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(shared);
            }
        }

        if (skillNames?.Any(s => !string.IsNullOrWhiteSpace(s)) == true)
        {
            list.Add(LoadSkillThroughPathTool);
        }

        if (knowledgeModuleIds?.Any(s => !string.IsNullOrWhiteSpace(s)) == true)
        {
            list.Add(KnowledgeSearchTool);
        }

        return list;
    }


    /// <summary>
    /// Leader-only allow-list entries. Shared <c>team_progress</c> / <c>team_memory</c> are not included.
    /// </summary>
    public static bool IsLeaderOrchestrationEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        if (string.Equals(entry, "team_*", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return LeaderExtraTools.Any(t => string.Equals(t, entry, StringComparison.OrdinalIgnoreCase));
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
