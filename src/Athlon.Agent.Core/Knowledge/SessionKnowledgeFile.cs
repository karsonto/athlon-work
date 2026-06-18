namespace Athlon.Agent.Core.Knowledge;

public sealed class SessionKnowledgeFile
{
    public bool Enabled { get; set; }
    public List<string> ModuleIds { get; set; } = [];
}

public sealed record SessionKnowledgeSnapshot(bool Enabled, IReadOnlySet<string> ModuleIds)
{
    public static SessionKnowledgeSnapshot Empty { get; } = new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

public interface ISessionKnowledgeState
{
    Task LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SaveAsync(string sessionId, SessionKnowledgeSnapshot state, CancellationToken cancellationToken = default);

    SessionKnowledgeSnapshot GetSnapshot(string? sessionId);

    bool ShouldExposeKnowledgeTool(string? sessionId);

    Task<IReadOnlySet<string>> GetModuleIdsAsync(string sessionId, CancellationToken cancellationToken = default);
}
