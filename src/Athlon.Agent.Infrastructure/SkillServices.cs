using Athlon.Agent.Core;
using Athlon.Agent.Skills;
using Athlon.Agent.Skills.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Infrastructure;

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
        services.AddSingleton<ISkillRuntime, SkillRuntime>();
        services.AddSingleton<IAvailableSkillsProvider>(sp => sp.GetRequiredService<ISkillRuntime>());
        return services;
    }
}
