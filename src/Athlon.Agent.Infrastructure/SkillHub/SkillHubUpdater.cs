using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure.SkillHub;

public sealed class SkillHubUpdater(
    ISkillHubClient client,
    SkillPackageInstaller installer,
    IAppLogger logger) : ISkillHubUpdater
{
    private readonly IAppLogger _logger = logger.ForContext("SkillHubUpdater");

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!client.IsConfigured)
        {
            return;
        }

        IReadOnlyList<RemoteSkillDto> skills;
        try
        {
            skills = await client.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning("Skill platform list failed: {Message}", ex.Message);
            return;
        }

        var changed = false;
        foreach (var skill in skills)
        {
            try
            {
                if (await installer.InstallAsync(skill, persistCatalog: false, cancellationToken).ConfigureAwait(false))
                {
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Skill update failed for {Id}: {Message}", skill.Id, ex.Message);
            }
        }

        if (changed)
        {
            try
            {
                await installer.PersistCatalogAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning("Skill catalog refresh failed: {Message}", ex.Message);
            }
        }
    }
}
