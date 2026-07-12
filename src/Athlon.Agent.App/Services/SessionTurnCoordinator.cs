using System.Windows;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
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

    public SessionTurnCoordinator(
        SessionTurnHost turnHost,
        QueuedTurnPresenter queuedTurnPresenter,
        SessionUiCache uiCache,
        IAgentSkillCatalog skillCatalog,
        ISkillRuntime skillRuntime,
        IMcpRegistry mcpRegistry)
    {
        _turnHost = turnHost;
        _queuedTurnPresenter = queuedTurnPresenter;
        _uiCache = uiCache;
        _skillCatalog = skillCatalog;
        _skillRuntime = skillRuntime;
        _mcpRegistry = mcpRegistry;
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
        SessionTurnUiController ui)
    {
        var request = new SessionTurnRequest(sessionId, session, input, imageAttachments, ui, IsAutoContinue: false);
        return _turnHost.TryStart(request, out var error) ? null : error ?? "无法开始生成。";
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
