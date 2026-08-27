using Athlon.Agent.App.Localization;
using Athlon.Agent.App.Navigation;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.Services.Speech;

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

        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IUserNotifier, UserNotifier>();
        services.AddSingleton<ITaskPlanCompletionNotifier, TaskPlanCompletionNotifier>();
        services.AddSingleton<IChatScrollService, ChatScrollService>();
        services.AddSingleton<ISpeechToTextService, SystemSpeechToTextService>();
        services.AddSingleton<MainWindowShutdownCoordinator>();

        services.AddSingleton<ComposerCoordinator>();

        services.AddSingleton<SessionHistoryCoordinator>();

        services.AddSingleton<SessionNavigationStore>();
        services.AddSingleton<SessionRuntimeStore>();
        services.AddSingleton<IConversationTranscriptWriter>(sp =>
            sp.GetRequiredService<SessionRuntimeStore>());

        services.AddSingleton<ApiKeySecretMigrationService>();

        services.AddSingleton(sp => new LayoutCoordinator(

            sp.GetRequiredService<IFileStorageService>(),

            sp.GetRequiredService<AppSettings>()));

        services.AddSingleton(sp => new NavigationCoordinator(

            sp.GetRequiredService<AppSettings>(),

            sp.GetRequiredService<AppSettings>().Sso.Enabled

                ? sp.GetService<IImpSsoSessionStore>()

                : null,

            sp.GetRequiredService<IUserNotifier>()));

        services.AddSingleton<SessionTurnCoordinator>();

        services.AddSingleton<SessionCompactionService>();

        services.AddSingleton<SubAgentCompletionContinuationService>();

        services.AddSingleton<ISubAgentCompletionNotifier>(sp =>
            sp.GetRequiredService<SubAgentCompletionContinuationService>());

        services.AddSingleton<SettingsViewModel>();

        services.AddSingleton(sp => new FileEditorViewModel(
            sp.GetRequiredService<WorkspaceFileEditorService>(),
            sp.GetRequiredService<WorkspaceGuard>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<IUserNotifier>()));

        services.AddSingleton(sp => new ContextSidebarViewModel(

            sp.GetRequiredService<IAppPathProvider>(),

            sp.GetRequiredService<IAgentSkillCatalog>(),

            sp.GetRequiredService<IMcpRegistry>(),

            sp.GetRequiredService<AppSettings>(),

            sp.GetRequiredService<ISshWorkspaceClient>()));

        services.AddSingleton(sp => new WorkspacePaneViewModel(
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<IActiveWorkspaceContext>(),
            sp.GetRequiredService<AppSettings>()));

        services.AddSingleton<Athlon.Agent.App.Services.Browser.BrowserWebViewRegistry>();
        services.AddSingleton<Athlon.Agent.Core.Browser.IBrowserWorkspaceState>(sp =>
            new Athlon.Agent.App.Services.Browser.BrowserWorkspaceState(
                sp.GetRequiredService<WorkspacePaneViewModel>()));
        services.AddSingleton<Athlon.Agent.Core.Browser.IBrowserAutomationHost>(sp =>
            new Athlon.Agent.App.Services.Browser.BrowserAutomationHost(
                sp.GetRequiredService<WorkspacePaneViewModel>(),
                sp.GetRequiredService<Athlon.Agent.App.Services.Browser.BrowserWebViewRegistry>()));
        services.AddSingleton<Athlon.Agent.App.Services.Terminal.TerminalSessionRegistry>();
        services.AddSingleton<Athlon.Agent.Core.Terminal.ITerminalWorkspaceState>(sp =>
            new Athlon.Agent.App.Services.Terminal.TerminalWorkspaceState(
                sp.GetRequiredService<WorkspacePaneViewModel>()));
        services.AddSingleton<Athlon.Agent.Core.Terminal.ITerminalAutomationHost>(sp =>
            new Athlon.Agent.App.Services.Terminal.TerminalAutomationHost(
                sp.GetRequiredService<WorkspacePaneViewModel>(),
                sp.GetRequiredService<Athlon.Agent.App.Services.Terminal.TerminalSessionRegistry>()));
        services.AddSingleton<Athlon.Agent.App.Services.ComputerUse.ComputerUseOverlayRegistry>();
        services.AddSingleton<Athlon.Agent.App.Services.ComputerUse.IComputerUseDesktopCaptureSession,
            Athlon.Agent.App.Services.ComputerUse.ComputerUseDesktopCaptureSession>();
        services.AddSingleton<Athlon.Agent.App.Services.ComputerUse.ComputerUseCaptureService>();
        services.AddSingleton<Athlon.Agent.App.Services.ComputerUse.ComputerUseUiAutomationService>();
        services.AddSingleton<Athlon.Agent.App.Services.ComputerUse.ComputerUseInputService>();
        services.AddSingleton<Athlon.Agent.Core.ComputerUse.IComputerUseAutomationHost>(sp =>
            new Athlon.Agent.App.Services.ComputerUse.ComputerUseAutomationHost(
                sp.GetRequiredService<Athlon.Agent.App.Services.ComputerUse.ComputerUseCaptureService>(),
                sp.GetRequiredService<Athlon.Agent.App.Services.ComputerUse.ComputerUseUiAutomationService>(),
                sp.GetRequiredService<Athlon.Agent.App.Services.ComputerUse.ComputerUseInputService>(),
                sp.GetRequiredService<Athlon.Agent.App.Services.ComputerUse.ComputerUseOverlayRegistry>(),
                sp.GetRequiredService<IImageAttachmentStore>(),
                sp.GetRequiredService<IAgentRunContextAccessor>(),
                sp.GetRequiredService<AuditLogService>()));

        services.AddSingleton(sp => new KnowledgeViewModel(
            sp.GetRequiredService<IKnowledgeStore>(),
            sp.GetRequiredService<IKnowledgeIndexer>(),
            sp.GetRequiredService<IKnowledgeSearchService>(),
            sp.GetRequiredService<IFileStorageService>(),
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<IUserNotifier>()));

        services.AddSingleton(sp => new ComposerKnowledgeViewModel(

            sp.GetRequiredService<ISessionKnowledgeState>(),

            sp.GetRequiredService<IKnowledgeStore>(),

            sp.GetRequiredService<AppSettings>(),

            sp.GetRequiredService<ILocalizationService>()));

        services.AddSingleton(sp => new ComposerHarnessViewModel(
            sp.GetRequiredService<ISessionHarnessState>(),
            sp.GetRequiredService<ISessionTaskListStore>(),
            sp.GetRequiredService<ITaskPlanCompletionNotifier>(),
            sp.GetRequiredService<ILocalizationService>()));

        services.AddSingleton<DebugActionBarViewModel>();
        services.AddSingleton<PlanActionBarViewModel>();

        services.AddSingleton<ChatPageViewModel>();

        services.AddSingleton<ScheduleViewModel>();

        services.AddSingleton<SkillHubViewModel>();

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



