using Athlon.Agent.Core;
using Athlon.Agent.Core.Prompt;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Infrastructure.Prompt;

public static class PromptServiceCollectionExtensions
{
    public static IServiceCollection AddAthlonEnvironmentPrompt(this IServiceCollection services)
    {
        services.AddSingleton<IEnvironmentPromptSection, BasePersonaSection>();
        services.AddSingleton<IEnvironmentPromptSection, HostEnvironmentSection>();
        services.AddSingleton<IEnvironmentPromptSection, EncodingPolicySection>();
        services.AddSingleton<IEnvironmentPromptSection, WorkspacePolicySection>();
        services.AddSingleton<IEnvironmentPromptSection, WorkspaceContextSection>();
        services.AddSingleton<IEnvironmentPromptSection, WorkspaceFilesSection>();
        services.AddSingleton<IEnvironmentPromptSection, FileToolsPolicySection>();
        services.AddSingleton<IEnvironmentPromptSection, ToolsPolicySection>();
        services.AddSingleton<IEnvironmentPromptSection, SkillsSection>();
        services.AddSingleton<IEnvironmentPromptSection, SubAgentDelegationSection>();
        services.AddSingleton<IEnvironmentPromptSection, ProductGuidanceSection>();
        services.AddSingleton<IEnvironmentPromptSection, SubAgentPersonaSection>();
        services.AddSingleton<IPreReasoningPromptContributor, WorkspaceFilesPromptContributor>();
        services.AddSingleton<ISystemPromptOrchestrator, SystemPromptOrchestrator>();
        return services;
    }
}
