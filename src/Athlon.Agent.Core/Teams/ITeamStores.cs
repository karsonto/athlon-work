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
