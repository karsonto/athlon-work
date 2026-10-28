namespace Athlon.Agent.Infrastructure.SkillHub;

public interface ISkillHubUpdater
{
    Task SyncAsync(CancellationToken cancellationToken = default);
}
