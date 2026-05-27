using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Compaction;
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
        services.AddSingleton<IAgentHostEnvironment, AgentHostEnvironment>();
        services.AddAthlonSkills();
        services.AddSingleton<IJsonFileStore>(jsonFileStore);
        services.AddSingleton<ICredentialStore, DpapiCredentialStore>();
        services.AddSingleton<IAppLogger>(logger);
        services.AddSingleton<IFileStorageService, FileStorageService>();
        services.AddHttpClient<IAgentModelClient, OpenAiCompatibleChatModelClient>();
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        services.AddSingleton<IAgentRuntime, AgentRuntime>();
        services.AddSingleton<IAgentEnvironmentPromptBuilder, AgentEnvironmentPromptBuilder>();
        services.AddSingleton<IMcpRegistry, McpRegistry>();
        services.AddSingleton<IToolRouter, CompositeToolRouter>();
        services.AddSingleton<IActiveWorkspaceContext, ActiveWorkspaceContext>();
        services.AddSingleton<IActiveAgentSessionContext, ActiveAgentSessionContext>();
        services.AddSingleton<ISessionHttpLogService, SessionHttpLogService>();
        services.AddSingleton<WorkspaceGuard>();
        services.AddSingleton<AuditLogService>();
        services.AddSingleton<IAgentTool, FileListTool>();
        services.AddSingleton<IAgentTool, FileReadTool>();
        services.AddSingleton<IAgentTool, FileWriteTool>();
        services.AddSingleton<IAgentTool, FileEditTool>();
        services.AddSingleton<IAgentTool, GrepFilesTool>();
        services.AddSingleton<IAgentTool, GlobFilesTool>();
        services.AddSingleton<IAgentTool, ExecuteCommandTool>();
        services.AddSingleton<MicrocompactService>();
        services.AddSingleton<IAutoCompactService, AutoCompactService>();
        services.AddSingleton<IPreCompletionPipeline, PreCompletionPipeline>();
        services.AddSingleton<IAgentTool, CompressAgentTool>();
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
        var mcpServers = McpConfigFileService.LoadServersAsync(paths).GetAwaiter().GetResult();
        if (mcpServers.Count > 0)
        {
            settings.McpServers = mcpServers;
        }

        return settings;
    }
}
