using Athlon.Agent.App.Navigation;

using Athlon.Agent.App.Services;

using Athlon.Agent.App.ViewModels;

using Athlon.Agent.Core;

using Athlon.Agent.Core.Harness;

using Athlon.Agent.Core.Knowledge;

using Athlon.Agent.Core.Sso;

using Athlon.Agent.Core.SubAgents;

using Athlon.Agent.Infrastructure;

using Athlon.Agent.Skills;

using Microsoft.Extensions.DependencyInjection;



namespace Athlon.Agent.App;



public static class ServiceCollectionExtensions

{

    public static IServiceCollection AddAthlonViewModels(this IServiceCollection services)

    {

        services.AddSingleton<IChatScrollService, ChatScrollService>();

        services.AddSingleton<ComposerCoordinator>();

        services.AddSingleton<SessionHistoryCoordinator>();

        services.AddSingleton<SessionNavigationStore>();

        services.AddSingleton<ApiKeySecretMigrationService>();

        services.AddSingleton(sp => new LayoutCoordinator(

            sp.GetRequiredService<IFileStorageService>(),

            sp.GetRequiredService<AppSettings>()));

        services.AddSingleton(sp => new NavigationCoordinator(

            sp.GetRequiredService<AppSettings>(),

            sp.GetRequiredService<AppSettings>().Sso.Enabled

                ? sp.GetService<IImpSsoSessionStore>()

                : null));

        services.AddSingleton<SessionTurnCoordinator>();

        services.AddSingleton<SubAgentCompletionContinuationService>();

        services.AddSingleton<ISubAgentCompletionNotifier>(sp =>
            sp.GetRequiredService<SubAgentCompletionContinuationService>());

        services.AddSingleton<SettingsViewModel>();

        services.AddSingleton(sp => new FileEditorViewModel(

            sp.GetRequiredService<WorkspaceFileEditorService>()));

        services.AddSingleton(sp => new ContextSidebarViewModel(

            sp.GetRequiredService<IAppPathProvider>(),

            sp.GetRequiredService<IAgentSkillCatalog>(),

            sp.GetRequiredService<IMcpRegistry>(),

            sp.GetRequiredService<AppSettings>()));

        services.AddSingleton(sp => new KnowledgeViewModel(

            sp.GetRequiredService<IKnowledgeStore>(),

            sp.GetRequiredService<IKnowledgeIndexer>(),

            sp.GetRequiredService<IKnowledgeSearchService>()));

        services.AddSingleton(sp => new ComposerKnowledgeViewModel(

            sp.GetRequiredService<ISessionKnowledgeState>(),

            sp.GetRequiredService<IKnowledgeStore>(),

            sp.GetRequiredService<AppSettings>()));

        services.AddSingleton(sp => new ComposerHarnessViewModel(

            sp.GetRequiredService<ISessionHarnessState>(),

            sp.GetRequiredService<ISessionTaskListStore>()));

        services.AddSingleton<ChatPageViewModel>();

        services.AddSingleton<ScheduleViewModel>();

        services.AddSingleton<PageViewFactory>();

        services.AddSingleton<MainShellViewModel>();

        services.AddSingleton<ISessionHost>(sp => sp.GetRequiredService<MainShellViewModel>());

        services.AddSingleton<Lazy<ISessionHost>>(sp =>
            new Lazy<ISessionHost>(() => sp.GetRequiredService<ISessionHost>()));

        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<MainShellViewModel>());

        services.AddSingleton<WebView2EnvironmentProvider>();

        services.AddSingleton<MainWindow>();

        return services;

    }

}



