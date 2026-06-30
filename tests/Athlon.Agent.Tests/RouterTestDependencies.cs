using Athlon.Agent.Core;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Core.Prompt;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Tests;

internal static class RouterTestDependencies
{
    public static ActiveAgentSessionContext CreateSessionContext()
    {
        var context = new ActiveAgentSessionContext();
        context.SetSession("test-session");
        return context;
    }

    public static AgentRunContextAccessor CreateRunContextAccessor(
        SessionAgentMode mode = SessionAgentMode.Agent,
        AgentRunKind kind = AgentRunKind.Root,
        string sessionId = "test-session")
    {
        var accessor = new AgentRunContextAccessor();
        if (mode == SessionAgentMode.Agent && kind == AgentRunKind.Root)
        {
            return accessor;
        }

        var session = AgentSession.Create("Test");
        session = session with { Id = sessionId };
        var runContext = AgentRunContext.CreateRoot(
            session,
            "run-1",
            new ToolRouter(Array.Empty<IAgentTool>()),
            PromptTestHelpers.CreateStaticOrchestrator(),
            []);
        if (kind == AgentRunKind.SubAgent)
        {
            runContext = runContext.CreateChild(
                "sub-session",
                new ToolRouter(Array.Empty<IAgentTool>()),
                PromptTestHelpers.CreateStaticOrchestrator(),
                "reviewer",
                null,
                null,
                null);
        }

        accessor.Push(runContext);
        return accessor;
    }

    public static AgentRunContextAccessor CreateRunContextAccessor(
        bool harnessEnabled,
        AgentRunKind kind = AgentRunKind.Root,
        string sessionId = "test-session") =>
        CreateRunContextAccessor(
            harnessEnabled ? SessionAgentMode.Coding : SessionAgentMode.Agent,
            kind,
            sessionId);

    public static ISessionKnowledgeState CreateSessionKnowledgeState(
        bool enabled = false,
        params string[] moduleIds) =>
        new StubSessionKnowledgeState(new SessionKnowledgeSnapshot(
            enabled,
            moduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase)));

    public static ISessionHarnessState CreateSessionHarnessState(SessionAgentMode mode = SessionAgentMode.Agent) =>
        new StubSessionHarnessState(new SessionHarnessSnapshot(mode));

    public static ISessionHarnessState CreateSessionHarnessState(bool enabled) =>
        CreateSessionHarnessState(enabled ? SessionAgentMode.Coding : SessionAgentMode.Agent);

    public static WorkspaceGuard CreateWorkspaceGuard(bool configured = true, string? workspaceRoot = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"athlon-router-test-{Guid.NewGuid():N}");
        var appDataRoot = Path.Combine(root, ".athlon-agent");
        Directory.CreateDirectory(appDataRoot);

        var context = new ActiveWorkspaceContext();
        var settings = new AppSettings();

        if (configured)
        {
            var wsRoot = workspaceRoot ?? Path.Combine(root, "workspace");
            Directory.CreateDirectory(wsRoot);
            context.SetWorkspace(wsRoot);
        }

        return new WorkspaceGuard(context, new AgentRunContextAccessor(), settings, new RouterTestPathProvider(appDataRoot));
    }

    internal sealed class StubSessionHarnessState(SessionHarnessSnapshot snapshot) : ISessionHarnessState
    {
        public Task LoadAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(string sessionId, SessionHarnessSnapshot state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public SessionHarnessSnapshot GetSnapshot(string? sessionId) => snapshot;

        public SessionAgentMode GetMode(string? sessionId) => snapshot.Mode;

        public bool IsCodingMode(string? sessionId) => snapshot.Mode == SessionAgentMode.Coding;

        public bool IsAskMode(string? sessionId) => snapshot.Mode == SessionAgentMode.Ask;

        public bool IsEnabled(string? sessionId) => IsCodingMode(sessionId);

        public bool IsCodingModeForActiveRun(IAgentRunContextAccessor runContextAccessor) =>
            IsActiveRun(runContextAccessor) && IsCodingMode(runContextAccessor.Current!.SessionId);

        public bool IsAskModeForActiveRun(IAgentRunContextAccessor runContextAccessor) =>
            IsActiveRun(runContextAccessor) && IsAskMode(runContextAccessor.Current!.SessionId);

        public bool IsEnabledForActiveRun(IAgentRunContextAccessor runContextAccessor) =>
            IsCodingModeForActiveRun(runContextAccessor);

        private static bool IsActiveRun(IAgentRunContextAccessor runContextAccessor)
        {
            var run = runContextAccessor.Current;
            return run is not null && run.Kind != AgentRunKind.SubAgent;
        }
    }

    internal sealed class StubSessionKnowledgeState(SessionKnowledgeSnapshot snapshot) : ISessionKnowledgeState
    {
        public Task LoadAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(string sessionId, SessionKnowledgeSnapshot state, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public SessionKnowledgeSnapshot GetSnapshot(string? sessionId) => snapshot;

        public bool ShouldExposeKnowledgeTool(string? sessionId) =>
            snapshot.Enabled && snapshot.ModuleIds.Count > 0;

        public Task<IReadOnlySet<string>> GetModuleIdsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(snapshot.Enabled ? snapshot.ModuleIds : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class RouterTestPathProvider(string rootPath) : IAppPathProvider
    {
        public string RootPath { get; } = rootPath;
        public string ConfigPath => Path.Combine(rootPath, "config");
        public string SessionsPath => Path.Combine(rootPath, "sessions");
        public string AuditPath => Path.Combine(rootPath, "audit");
        public string LogsPath => Path.Combine(rootPath, "logs");
        public string CredentialsPath => Path.Combine(rootPath, "credentials");
        public string SkillsPath => Path.Combine(rootPath, "skills");

        public void EnsureCreated() { }

        public string ResolveSkillPath(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(SkillsPath, path);
    }
}
