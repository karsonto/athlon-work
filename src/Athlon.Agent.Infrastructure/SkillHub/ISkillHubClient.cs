namespace Athlon.Agent.Infrastructure.SkillHub;

public interface ISkillHubClient
{
    /// <summary>True when BehaviorReport.BaseUrl is configured.</summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<RemoteSkillDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<byte[]> DownloadAsync(string skillId, CancellationToken cancellationToken = default);
}
