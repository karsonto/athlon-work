using Athlon.Agent.Core;
using Athlon.Agent.Skills;
using Athlon.Agent.Skills.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Infrastructure;

public sealed class AvailableSkillsProvider(IAgentSkillCatalog catalog) : IAvailableSkillsProvider
{
    public IReadOnlyList<AvailableSkillInfo> GetSkills() =>
        catalog.Skills
            .Select(skill => new AvailableSkillInfo(skill.Name, skill.Description, skill.SkillId))
            .OrderBy(skill => skill.Name, StringComparer.Ordinal)
            .ToArray();
}

public static class SkillServiceCollectionExtensions
{
    public static IServiceCollection AddAthlonSkills(this IServiceCollection services)
    {
        services.AddSingleton<IAgentSkillRepository>(sp =>
        {
            var paths = sp.GetRequiredService<IAppPathProvider>();
            return new FileSystemSkillRepository(paths.SkillsPath);
        });
        services.AddSingleton<IAgentSkillCatalog, AgentSkillCatalog>();
        services.AddSingleton<IAvailableSkillsProvider, AvailableSkillsProvider>();
        return services;
    }
}
