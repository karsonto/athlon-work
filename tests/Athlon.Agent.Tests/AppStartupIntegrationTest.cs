using Athlon.Agent.App;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.SlashCommands;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.SubAgents;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Tests;

public sealed class AppStartupIntegrationTest
{
    [Fact]
    public void AddAthlonInfrastructure_ResolvesRuntimeAndSessionsSpawnTool_WithoutCycle()
    {
        var services = new ServiceCollection();
        services.AddAthlonInfrastructure();

        var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IAgentRuntime>();
        var orchestrator = provider.GetRequiredService<IAgentOrchestrator>();
        var router = provider.GetRequiredService<IToolRouter>();
        var spawnTool = provider.GetServices<IAgentTool>()
            .OfType<SessionsSpawnTool>()
            .Single();

        Assert.NotNull(runtime);
        Assert.NotNull(orchestrator);
        Assert.NotNull(router);
        Assert.Equal("sessions_spawn", spawnTool.Definition.Name);

        if (provider is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void AddAthlonViewModels_RegistersMainShellAndChatPage_WithoutCycle()
    {
        var services = new ServiceCollection();
        services.AddAthlonInfrastructure();
        services.AddSingleton(sp => new SessionUiCache(
            System.Windows.Threading.Dispatcher.CurrentDispatcher,
            sp.GetRequiredService<AppSettings>()));
        services.AddSingleton<SessionTurnHost>();
        services.AddSingleton<QueuedTurnPresenter>();
        services.AddSingleton<ComposerAtCompletionService>();
        services.AddSingleton<IComposerSlashCommandRegistry>(sp =>
            new ComposerSlashCommandRegistry(sp.GetServices<IComposerSlashCommand>()));
        services.AddSingleton<ComposerSlashCommandExecutor>();
        services.AddSingleton<SchedulerService>();
        services.AddSingleton<ApplicationShutdownService>();
        services.AddSingleton<ClipboardImageAttachmentReader>();
        services.AddSingleton<AppUpdateService>();
        services.AddSingleton<IMcpRegistry>(new TestMcpRegistry());
        services.AddAthlonViewModels();

        Assert.Contains(services, d => d.ServiceType == typeof(MainShellViewModel));
        Assert.Contains(services, d => d.ServiceType == typeof(ChatPageViewModel));
        Assert.Contains(services, d => d.ServiceType == typeof(ISessionHost));
        Assert.Contains(services, d => d.ServiceType == typeof(INavigationService));

        using var provider = services.BuildServiceProvider();
        var chatPage = provider.GetRequiredService<ChatPageViewModel>();
        var settings = provider.GetRequiredService<SettingsViewModel>();
        Assert.NotNull(chatPage);
        Assert.NotNull(settings);
    }
}
