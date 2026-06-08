using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Infrastructure.Memory;
using Athlon.Agent.Core.ComposerCommands;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Infrastructure.ComposerCommands;
using Athlon.Agent.Core.Licensing;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Infrastructure.Licensing;
using Athlon.Agent.Infrastructure.Prompt;
using Athlon.Agent.Infrastructure.SubAgents;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Athlon.Agent.Infrastructure;

public static class ServiceCollectionExtensions
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddAthlonInfrastructure(this IServiceCollection services)
    {
        var paths = new AppPathProvider();
        paths.EnsureCreated();
        var jsonFileStore = new JsonFileStore();
        var settingsPath = Path.Combine(paths.ConfigPath, "settings.json");
        var settings = LoadSettings(settingsPath) ?? new AppSettings();
        var logger = AppLogger.Create(settings.Logging, paths.LogsPath);

        services.AddSingleton(settings);
        services.AddSingleton<IAppPathProvider>(paths);
        services.AddSingleton<IAdAccountResolver, FallbackAdAccountResolver>();
        services.AddSingleton<ILicenseValidator, LicenseValidator>();
        services.AddSingleton<ILicenseStore, LicenseStore>();
        services.AddSingleton<IAgentHostEnvironment, AgentHostEnvironment>();
        services.AddAthlonSkills();
        services.AddAthlonEnvironmentPrompt();
        services.AddSingleton<IJsonFileStore>(jsonFileStore);
        services.AddSingleton<ICredentialStore, DpapiCredentialStore>();
        services.AddSingleton<IAppLogger>(logger);
        services.AddSingleton<IFileStorageService, FileStorageService>();
        services.AddHttpClient<IAgentModelClient, OpenAiCompatibleChatModelClient>(
            static client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        services.AddSingleton<IAgentRuntime, AgentRuntime>();
        services.AddSingleton<IImageAttachmentReader, ImageAttachmentReader>();
        services.AddSingleton<IImageAttachmentStore, ImageAttachmentStore>();
        services.AddSingleton<IMcpRegistry, McpRegistry>();
        services.AddSingleton<IToolRouter, CompositeToolRouter>();
        services.AddSingleton<IActiveWorkspaceContext, ActiveWorkspaceContext>();
        services.AddSingleton<IActiveAgentSessionContext, ActiveAgentSessionContext>();
        services.AddSingleton<ISessionHttpLogService, SessionHttpLogService>();
        services.AddSingleton<WorkspaceGuard>();
        services.AddSingleton<WorkspaceFileEditorService>();
        services.AddSingleton<AuditLogService>();
        services.AddSingleton<ExecuteCommandProcessRegistry>();
        services.AddSingleton<IAgentTool, FileListTool>();
        services.AddSingleton<IAgentTool, FileReadTool>();
        services.AddSingleton<IAgentTool, FileWriteTool>();
        services.AddSingleton<IAgentTool, FileEditTool>();
        services.AddSingleton<IAgentTool, GrepFilesTool>();
        services.AddSingleton<IAgentTool, GlobFilesTool>();
        services.AddSingleton<IAgentTool, ExecuteCommandTool>();
        services.AddSingleton<IAgentTool, LoadSkillThroughPathTool>();
        services.AddSingleton<Lazy<ChildAgentToolRouter>>(static sp => new Lazy<ChildAgentToolRouter>(() =>
            new ChildAgentToolRouter(
                sp.GetServices<IAgentTool>().Where(tool => tool is not IExcludedFromChildAgentToolkit),
                sp.GetRequiredService<IMcpRegistry>())));
        services.AddSingleton<SubAgentSystemPromptOrchestrator>();
        services.AddSingleton<ISubAgentSessionStore, FileSubAgentSessionStore>();
        if (settings.SubAgent.Enabled)
        {
            services.AddSingleton<SubAgentTool>();
            services.AddSingleton<IAgentTool>(static sp => sp.GetRequiredService<SubAgentTool>());
        }

        services.AddSingleton<TruncateArgsService>();
        services.AddSingleton<ITokenEstimatorCalibrator, TokenEstimatorCalibrator>();
        services.AddSingleton<IConversationCompactor, ConversationCompactor>();
        services.AddSingleton<IToolResultEvictor, ToolResultEvictor>();
        services.AddSingleton<IPreCompletionPipeline, PreCompletionPipeline>();
        services.AddSingleton<ISessionCompactionService, SessionCompactionService>();
        services.AddSingleton<IComposerCommand, CompactComposerCommand>();
        services.AddSingleton<IComposerCommand, HelpComposerCommand>();
        services.AddSingleton<IComposerCommandRegistry, ComposerCommandRegistry>();
        services.AddSingleton<ComposerCommandExecutor>();
        // Long-term memory services
        services.AddSingleton<ILongTermMemory, FileLongTermMemory>();
        services.AddSingleton<MemoryFlushService>();
        services.AddSingleton<MemoryConsolidationService>();
        services.AddSingleton<IPostTurnMemoryProcessor, PostTurnMemoryProcessor>();
        services.AddSingleton<IAgentTool, MemorySearchTool>();
        services.AddSingleton<IAgentTool, MemoryGetTool>();
        services.AddSingleton<IPreReasoningPromptContributor, MemoryPromptContributor>();
        return services;
    }

    private static AppSettings? LoadSettings(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        var json = File.ReadAllText(settingsPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonFileStore.Options);
        if (settings is null)
        {
            return null;
        }

        var paths = new AppPathProvider();
        var mcpServers = McpConfigFileService.LoadServers(paths);
        if (mcpServers.Count > 0)
        {
            settings.McpServers = mcpServers;
        }

        var skills = SkillConfigFileService.LoadSkills(paths);
        if (skills.Count > 0)
        {
            settings.Skills = skills;
        }

        return settings;
    }
}
