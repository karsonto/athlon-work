using System.Diagnostics;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Infrastructure.BehaviorReport;
using Microsoft.Extensions.DependencyInjection;

namespace Athlon.Agent.Infrastructure.SubAgents;

public sealed class SubAgentRunExecutor(
    AppSettings settings,
    IServiceProvider serviceProvider,
    IFileStorageService storage,
    ISubAgentSessionStore sessionStore,
    Lazy<ChildAgentToolRouter> childToolRouter,
    SubAgentSystemPromptOrchestrator subAgentPromptOrchestrator,
    IActiveAgentSessionContext activeSessionContext,
    IAgentRunContextAccessor runContextAccessor,
    IAppLogger logger)
{
    private readonly SubAgentSettings _subAgent = settings.SubAgent;
    private readonly IAppLogger _logger = logger.ForContext("SubAgentRunExecutor");

    public async Task<SubAgentRunOutcome> ExecuteAsync(
        string parentSessionId,
        string subSessionId,
        string role,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (_subAgent.MaxNestingDepth > 0 && SubAgentExecutionScope.CurrentDepth >= _subAgent.MaxNestingDepth)
        {
            return SubAgentRunOutcome.Fail($"Sub-agent nesting depth limit ({_subAgent.MaxNestingDepth}) reached.");
        }

        var bundle = await sessionStore.LoadAsync(parentSessionId, subSessionId, cancellationToken).ConfigureAwait(false);
        var session = bundle?.Session ?? await CreateSubSessionAsync(parentSessionId, subSessionId, cancellationToken).ConfigureAwait(false);
        bundle = new SubAgentSessionBundle(session, role);

        var ignorePatterns = ResolveIgnorePatterns(session);
        var workspaceRoot = string.IsNullOrWhiteSpace(session.ActiveWorkspace)
            ? runContextAccessor.Current?.WorkspaceRoot
            : Path.GetFullPath(session.ActiveWorkspace);
        var parentRunContext = runContextAccessor.Current
            ?? AgentRunContext.CreateRoot(
                new AgentSession(parentSessionId, string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, workspaceRoot, null, settings.Model.ModelName, []),
                Guid.NewGuid().ToString("N"),
                childToolRouter.Value,
                subAgentPromptOrchestrator,
                ignorePatterns);
        var childContext = parentRunContext.CreateChild(
            subSessionId,
            childToolRouter.Value,
            subAgentPromptOrchestrator,
            role,
            new AgentLoopOptions { MaxModelToolRounds = _subAgent.MaxToolRounds },
            workspaceRoot,
            ignorePatterns);

        using var depthScope = SubAgentExecutionScope.Enter();
        using var runScope = runContextAccessor.Push(childContext);
        using var sessionScope = activeSessionContext.Enter(subSessionId);
        using var workspaceScope = SessionWorkspaceScope.Enter(childContext.WorkspaceRoot, childContext.WorkspaceIgnorePatterns);

        _logger.Information(
            "Starting sub-agent run parent={ParentId} sub={SubId} continue={Continue}",
            parentSessionId,
            subSessionId,
            bundle.Session.Messages.Count > 0);

        RecordSubagent("started", role, parentSessionId, subSessionId);
        var sw = Stopwatch.StartNew();
        try
        {
            var orchestrator = serviceProvider.GetRequiredService<IAgentOrchestrator>();
            session = await orchestrator.SendAsync(session, message.Trim(), null, null, cancellationToken).ConfigureAwait(false);
            bundle = bundle with { Session = session };
            await sessionStore.SaveAsync(parentSessionId, subSessionId, bundle, cancellationToken).ConfigureAwait(false);

            var responseText = SubAgentResultFormatter.ExtractLastAssistantText(session)
                ?? "(Sub-agent finished without assistant text.)";
            sw.Stop();
            RecordSubagent("completed", role, parentSessionId, subSessionId, sw.ElapsedMilliseconds, "ok");
            return SubAgentRunOutcome.Ok(responseText, session);
        }
        catch (OperationCanceledException)
        {
            await sessionStore.SaveAsync(parentSessionId, subSessionId, bundle, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.Error(ex, "Sub-agent run failed parent={ParentId} sub={SubId}", parentSessionId, subSessionId);
            RecordSubagent("failed", role, parentSessionId, subSessionId, sw.ElapsedMilliseconds, errorType: ex.GetType().Name);
            return SubAgentRunOutcome.Fail(ex.Message);
        }
    }

    private static void RecordSubagent(
        string action,
        string role,
        string parentSessionId,
        string sessionId,
        long? latencyMs = null,
        string? outcome = null,
        string? errorType = null)
    {
        try
        {
            EventManager.Instance.Record(
                BehaviorEventIds.Subagent,
                action is "started" ? BehaviorEventTypes.Action : BehaviorEventTypes.Event,
                BehaviorEventIds.Subagent,
                new Dictionary<string, object?>
                {
                    ["action"] = action,
                    ["role"] = role,
                    ["parent_session_id"] = parentSessionId,
                    ["session_id"] = sessionId,
                    ["latency_ms"] = latencyMs,
                    ["outcome"] = outcome,
                    ["error_type"] = errorType
                });
        }
        catch
        {
            // ignore
        }
    }

    private async Task<AgentSession> CreateSubSessionAsync(
        string parentSessionId,
        string subSessionId,
        CancellationToken cancellationToken)
    {
        var parent = await storage.LoadSessionAsync(parentSessionId, cancellationToken).ConfigureAwait(false);
        var workspace = parent?.ActiveWorkspace ?? SessionWorkspaceScope.CurrentState?.RootPath;
        var activeSkill = parent?.ActiveSkill;
        return new AgentSession(
            subSessionId,
            "Sub-agent",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            workspace,
            activeSkill,
            parent?.ModelName ?? settings.Model.ModelName,
            Array.Empty<ChatMessage>());
    }

    private IReadOnlyList<string> ResolveIgnorePatterns(AgentSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.ActiveWorkspace))
        {
            var fullPath = Path.GetFullPath(session.ActiveWorkspace);
            var match = settings.Workspaces.FirstOrDefault(workspace =>
                !string.IsNullOrWhiteSpace(workspace.RootPath)
                && string.Equals(Path.GetFullPath(workspace.RootPath), fullPath, StringComparison.OrdinalIgnoreCase));
            return WorkspaceIgnoreResolver.Resolve(
                workspacePatterns: match?.IgnorePatterns,
                globalPatterns: settings.WorkspaceIgnore.DirectoryNames);
        }

        var configuredWorkspace = settings.Workspaces.FirstOrDefault(workspace => !string.IsNullOrWhiteSpace(workspace.RootPath));
        return WorkspaceIgnoreResolver.Resolve(
            workspacePatterns: configuredWorkspace?.IgnorePatterns,
            globalPatterns: settings.WorkspaceIgnore.DirectoryNames);
    }
}

public sealed record SubAgentRunOutcome(bool IsSuccess, string? ResultText, string? Error, AgentSession? Session)
{
    public static SubAgentRunOutcome Ok(string resultText, AgentSession session) =>
        new(true, resultText, null, session);

    public static SubAgentRunOutcome Fail(string error) =>
        new(false, null, error, null);
}
