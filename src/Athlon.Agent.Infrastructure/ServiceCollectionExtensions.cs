using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Infrastructure.Harness;
using Athlon.Agent.Infrastructure.Memory;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Core.Licensing;
using Athlon.Agent.Core.Middleware;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure.Knowledge;
using Athlon.Agent.Infrastructure.Licensing;
using Athlon.Agent.Infrastructure.Prompt;
using Athlon.Agent.Infrastructure.Sso;
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
        services.AddSingleton<IImpSsoSessionStore, ImpSsoSessionStore>();
        services.AddHttpClient<IImpSsoAuthService, ImpSsoAuthService>(
            static client => client.Timeout = TimeSpan.FromSeconds(30));
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
        services.AddSingleton<IAgentRunContextAccessor, AgentRunContextAccessor>();
        services.AddSingleton<IActiveWorkspaceContext, ActiveWorkspaceContext>();
        services.AddSingleton<IActiveAgentSessionContext, ActiveAgentSessionContext>();
        services.AddSingleton<ISessionHttpLogService, SessionHttpLogService>();
        services.AddSingleton<WorkspaceGuard>();
        services.AddSingleton<WorkspaceFileEditorService>();
        services.AddSingleton<KnowledgeDocumentExtractor>();
        services.AddSingleton<KnowledgeChunker>();
        services.AddSingleton<IKnowledgeStore, SqliteKnowledgeStore>();
        services.AddSingleton<IKnowledgeIndexer, KnowledgeIndexerService>();
        services.AddSingleton<IKnowledgeSearchService, KnowledgeSearchService>();
        services.AddSingleton<ISessionKnowledgeState, SessionKnowledgeState>();
        services.AddSingleton<ISessionHarnessState, SessionHarnessState>();
        services.AddSingleton<ISessionTaskListStore, FileSessionTaskListStore>();
        services.AddHttpClient<IEmbeddingClient, OpenAiCompatibleEmbeddingClient>(
            static client => client.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<AuditLogService>();
        services.AddSingleton<ExecuteCommandProcessRegistry>();
        services.AddSingleton<IAgentTool, FileListTool>();
        services.AddSingleton<IAgentTool, FileReadTool>();
        services.AddSingleton<IAgentTool, FileWriteTool>();
        services.AddSingleton<IAgentTool, FileEditTool>();
        services.AddSingleton<IAgentTool, ApplyPatchTool>();
        services.AddSingleton<IAgentTool, GrepFilesTool>();
        services.AddSingleton<IAgentTool, GlobFilesTool>();
        services.AddSingleton<IAgentTool, ExecuteCommandTool>();
        services.AddSingleton<IAgentTool, KnowledgeSearchTool>();
        services.AddSingleton<IAgentTool, LoadSkillThroughPathTool>();
        services.AddSingleton<Lazy<ChildAgentToolRouter>>(static sp => new Lazy<ChildAgentToolRouter>(() =>
            new ChildAgentToolRouter(
                sp.GetServices<IAgentTool>(),
                sp.GetRequiredService<IMcpRegistry>(),
                sp.GetRequiredService<AppSettings>(),
                sp.GetRequiredService<IActiveAgentSessionContext>(),
                sp.GetRequiredService<ISessionKnowledgeState>(),
                sp.GetRequiredService<ISessionHarnessState>(),
                sp.GetRequiredService<IAgentRunContextAccessor>(),
                sp.GetRequiredService<WorkspaceGuard>())));
        services.AddSingleton<SubAgentSystemPromptOrchestrator>();
        services.AddSingleton<ISubAgentSessionStore, FileSubAgentSessionStore>();
        services.AddSingleton<ISubAgentRegistry, FileSubAgentRegistry>();
        services.AddSingleton<ISubAgentTaskStore, FileSubAgentTaskStore>();
        services.AddSingleton<ISubAgentCompletionStore, FileSubAgentCompletionStore>();
        services.AddSingleton<SubAgentRunExecutor>();
        services.AddSingleton<SubAgentBackgroundExecutor>();
        services.AddSingleton<ISubAgentSessionManager, SubAgentSessionManager>();
        services.AddSingleton<Lazy<ISubAgentSessionManager>>(static sp =>
            new Lazy<ISubAgentSessionManager>(() => sp.GetRequiredService<ISubAgentSessionManager>()));
        if (settings.SubAgent.Enabled)
        {
            services.AddSingleton<SubAgentTool>();
            services.AddSingleton<IAgentTool>(static sp => sp.GetRequiredService<SubAgentTool>());
            services.AddSingleton<IAgentTool, SessionsSpawnTool>();
            services.AddSingleton<IAgentTool, SessionsSendTool>();
            services.AddSingleton<IAgentTool, SessionsListTool>();
            services.AddSingleton<IAgentTool, SessionsHistoryTool>();
            services.AddSingleton<IAgentTool, SessionsPendingCompletionsTool>();
            services.AddSingleton<IAgentTool, SubAgentTaskOutputTool>();
            services.AddSingleton<IPreReasoningPromptContributor, SubAgentCompletionPromptContributor>();
        }

        services.AddSingleton<TruncateArgsService>();
        services.AddSingleton<ITokenEstimatorCalibrator, TokenEstimatorCalibrator>();
        services.AddSingleton<ISessionUsageAccumulator, SessionUsageAccumulator>();
        services.AddSingleton<IPromptPressureStore, PromptPressureStore>();
        services.AddSingleton<ISessionToolStormStore, SessionToolStormStore>();
        services.AddSingleton<IConversationCompactor, ConversationCompactor>();
        services.AddSingleton<IToolResultEvictor, ToolResultEvictor>();
        services.AddSingleton<IPreCompletionPipeline, PreCompletionPipeline>();
        // Long-term memory services
        services.AddSingleton<ILongTermMemory, FileLongTermMemory>();
        services.AddSingleton<MemoryFlushService>();
        services.AddSingleton<MemoryConsolidationService>();
        services.AddSingleton<IPostTurnMemoryProcessor, PostTurnMemoryProcessor>();
        services.AddSingleton<IAgentTool, MemorySearchTool>();
        services.AddSingleton<IAgentTool, MemoryGetTool>();
        services.AddSingleton<IAgentTool, TodoWriteTool>();
        services.AddSingleton<IPreReasoningPromptContributor, MemoryPromptContributor>();
        services.AddSingleton<IPreReasoningPromptContributor, TaskListPromptContributor>();
        services.AddSingleton<CompactionTurnMiddleware>();
        services.AddSingleton<IAgentTurnMiddleware, CompactionTurnMiddleware>();
        services.AddSingleton<IAgentTurnMiddleware, PostTurnMemoryMiddleware>();
        services.AddSingleton<IAgentTurnMiddleware, ToolStormTurnMiddleware>();
        services.AddSingleton<AgentTurnMiddlewarePipeline>();
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

        return settings;
    }
}
