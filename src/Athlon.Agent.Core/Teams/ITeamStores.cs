namespace Athlon.Agent.Core.Teams;

public interface IAgentProfileStore
{
    Task<IReadOnlyList<AgentProfile>> ListAsync(CancellationToken cancellationToken = default);

    Task<AgentProfile?> GetAsync(string profileId, CancellationToken cancellationToken = default);

    Task SaveAsync(AgentProfile profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(string profileId, CancellationToken cancellationToken = default);
}

public interface ITeamDefinitionStore
{
    Task<IReadOnlyList<TeamDefinition>> ListAsync(CancellationToken cancellationToken = default);

    Task<TeamDefinition?> GetAsync(string teamId, CancellationToken cancellationToken = default);

    Task SaveAsync(TeamDefinition team, CancellationToken cancellationToken = default);

    Task DeleteAsync(string teamId, CancellationToken cancellationToken = default);
}

public interface ITeamTaskStore
{
    Task<TeamTask?> GetAsync(string rootSessionId, string taskId, CancellationToken cancellationToken = default);

    Task SaveAsync(TeamTask task, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamTask>> ListForRootSessionAsync(string rootSessionId, CancellationToken cancellationToken = default);

    string ResolveTaskDirectory(string rootSessionId, string taskId);

    string ResolveMemberDirectory(string rootSessionId, string taskId, string memberSessionId);

    /// <summary>
    /// Stable Leader transcript directory for a Chat root session + Team
    /// (survives multiple Team Tasks so multi-turn Teams keep one Leader conversation).
    /// </summary>
    string ResolveLeaderDirectory(string rootSessionId, string teamId);

    /// <summary>Deterministic Leader session id for <paramref name="teamId"/> within a root Chat session.</summary>
    string StableLeaderSessionId(string teamId);
}

public interface ITeamTraceStore
{
    Task AppendAsync(string rootSessionId, string taskId, TeamTraceEvent evt, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamTraceEvent>> ReadAsync(string rootSessionId, string taskId, CancellationToken cancellationToken = default);

    Task SaveLayoutAsync(
        string rootSessionId,
        string taskId,
        IReadOnlyDictionary<string, TeamTraceNodeLayout> layout,
        CancellationToken cancellationToken = default);
}

public sealed class TeamMemoryEntry
{
    public string Key { get; set; } = "";

    public string Content { get; set; } = "";

    public List<string> Tags { get; set; } = new();

    public string? AuthorProfileId { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public interface ITeamMemoryStore
{
    const int MaxContentChars = 4000;
    const int MaxEntries = 200;

    Task PutAsync(
        string rootSessionId,
        string taskId,
        TeamMemoryEntry entry,
        CancellationToken cancellationToken = default);

    Task<TeamMemoryEntry?> GetAsync(
        string rootSessionId,
        string taskId,
        string key,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamMemoryEntry>> ListAsync(
        string rootSessionId,
        string taskId,
        int limit = 40,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string rootSessionId,
        string taskId,
        string key,
        CancellationToken cancellationToken = default);
}
