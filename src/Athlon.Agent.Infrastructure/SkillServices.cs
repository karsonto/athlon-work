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
            var logger = sp.GetRequiredService<IAppLogger>().ForContext("SkillRepository");
            return new FileSystemSkillRepository(
                paths.SkillsPath,
                (dir, ex) => logger.Warning(
                    $"Skill load failed ({Path.GetFileName(dir)}): {ex.Message} [{dir}]"));
        });
        services.AddSingleton<IAgentSkillCatalog, AgentSkillCatalog>();
        services.AddSingleton<ISkillRuntime, SkillRuntime>();
        services.AddSingleton<IAvailableSkillsProvider>(sp => sp.GetRequiredService<ISkillRuntime>());
        return services;
    }
}
