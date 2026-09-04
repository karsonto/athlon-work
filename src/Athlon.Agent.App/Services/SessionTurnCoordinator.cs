using System.Windows;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Plan;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Skills;

namespace Athlon.Agent.App.Services;

public sealed class SessionTurnCoordinator
{
    private readonly SessionTurnHost _turnHost;
    private readonly QueuedTurnPresenter _queuedTurnPresenter;
    private readonly SessionUiCache _uiCache;
    private readonly IAgentSkillCatalog _skillCatalog;
    private readonly ISkillRuntime _skillRuntime;
    private readonly IMcpRegistry _mcpRegistry;
    private readonly IUserQuestionState _userQuestions;

    public SessionTurnCoordinator(
        SessionTurnHost turnHost,
        QueuedTurnPresenter queuedTurnPresenter,
        SessionUiCache uiCache,
        IAgentSkillCatalog skillCatalog,
        ISkillRuntime skillRuntime,
        IMcpRegistry mcpRegistry,
        IUserQuestionState userQuestions)
    {
        _turnHost = turnHost;
        _queuedTurnPresenter = queuedTurnPresenter;
        _uiCache = uiCache;
        _skillCatalog = skillCatalog;
        _skillRuntime = skillRuntime;
        _mcpRegistry = mcpRegistry;
        _userQuestions = userQuestions;
    }

    public SessionTurnHost TurnHost => _turnHost;

    public QueuedTurnPresenter QueuedTurnPresenter => _queuedTurnPresenter;

    public bool IsRunning(string sessionId) => _turnHost.IsRunning(sessionId);

    public bool HasActiveWork => _turnHost.HasActiveWork;

    public void Cancel(string sessionId) => _turnHost.Cancel(sessionId);

    public void ClearQueue(string sessionId) => _turnHost.ClearQueue(sessionId);

    public SessionTurnUiController GetOrCreateUi(
        string sessionId,
        Action requestScroll,
        Action requestScrollImmediate) =>
        _uiCache.GetOrCreate(sessionId, requestScroll, requestScrollImmediate);

    public void RemoveUiCache(string sessionId) => _uiCache.Remove(sessionId);

    public string? TryStartTurn(
        string sessionId,
        AgentSession session,
        string input,
        ImageAttachment[] imageAttachments,
        SessionTurnUiController ui,
        bool computerUseActive = false,
        bool appendUserMessage = true)
    {
        var request = new SessionTurnRequest(
            sessionId,
            session,
            input,
            imageAttachments,
            ui,
            IsAutoContinue: false,
            ComputerUseActive: computerUseActive,
            AppendUserMessage: appendUserMessage);
        if (_turnHost.TryStart(request, out var error))
        {
            // The turn consumed (or abandoned) any pending ask_user question.
            _userQuestions.Clear(sessionId);
            return null;
        }

        return error ?? "无法开始生成。";
    }

    public string? TryStartDebugContinuation(
        string sessionId,
        AgentSession session,
        DebugContinuationKind continuation,
        SessionTurnUiController ui)
    {
        var request = new SessionTurnRequest(
            sessionId,
            session,
            string.Empty,
            Array.Empty<ImageAttachment>(),
            ui,
            DebugContinuation: continuation);
        return _turnHost.TryStart(request, out var error) ? null : error ?? "无法继续 Debug 流程。";
    }

    public string? TryStartPlanContinuation(
        string sessionId,
        AgentSession session,
        PlanContinuationKind continuation,
        SessionTurnUiController ui,
        string userInput = "")
    {
        var request = new SessionTurnRequest(
            sessionId,
            session,
            userInput,
            Array.Empty<ImageAttachment>(),
            ui,
            PlanContinuation: continuation);
        if (_turnHost.TryStart(request, out var error))
        {
            // The plan continuation consumed any pending ask_user question.
            _userQuestions.Clear(sessionId);
            return null;
        }

        return error ?? "无法继续 Plan 流程。";
    }

    public void EnqueueTurn(
        string sessionId,
        string input,
        ImageAttachment[] imageAttachments,
        SessionTurnUiController ui)
    {
        var queueId = Guid.NewGuid().ToString("N");
        _queuedTurnPresenter.Enqueue(sessionId, queueId, input, imageAttachments, ui);
    }

    public string ExpandComposerInput(string composerText)
    {
        var expanded = McpComposerExpander.Expand(composerText, _mcpRegistry);
        return SkillComposerExpander.Expand(expanded, _skillRuntime.GetSkills());
    }

    public void ReloadSkills() => _skillCatalog.Reload();

    public async Task HandleTurnCompletedAsync(
        SessionTurnCompletedEventArgs e,
        string displayedSessionId,
        Func<AgentSession> getDisplayedSession,
        Action<AgentSession> setDisplayedSession,
        Action<string> setCurrentTitle,
        Func<AgentSession, Task> saveSessionAsync,
        Action requestRefreshHistory,
        Action notifyCommandStates,
        Action<string?> setStatusOnError)
    {
        if (string.Equals(e.SessionId, displayedSessionId, StringComparison.Ordinal))
        {
            setDisplayedSession(e.Session);
            setCurrentTitle(e.Session.Title);
        }

        await saveSessionAsync(
            string.Equals(e.SessionId, displayedSessionId, StringComparison.Ordinal)
                ? getDisplayedSession()
                : e.Session);
        requestRefreshHistory();
        if (_queuedTurnPresenter.TryProcessNext(e, out var queueError)
            && string.Equals(e.SessionId, displayedSessionId, StringComparison.Ordinal)
            && queueError is not null)
        {
            setStatusOnError(queueError);
        }

        notifyCommandStates();
    }

    public void HandleTurnStateChanged(string sessionId, string displayedSessionId, Action onDisplayedBusyChanged, Action requestRefreshHistory)
    {
        if (string.Equals(sessionId, displayedSessionId, StringComparison.Ordinal))
        {
            onDisplayedBusyChanged();
        }

        requestRefreshHistory();
    }
}
