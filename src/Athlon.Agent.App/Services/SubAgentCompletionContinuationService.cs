using System.Collections.Concurrent;
using System.Windows;
using Athlon.Agent.Core;
using Athlon.Agent.Core.BehaviorReport;
using Athlon.Agent.Core.SubAgents;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.BehaviorReport;

namespace Athlon.Agent.App.Services;

/// <summary>
/// When a background sub-agent finishes, starts an auto-continue parent turn so completions
/// are drained into the prompt and the user sees a summary without sending another message.
/// </summary>
public sealed class SubAgentCompletionContinuationService : ISubAgentCompletionNotifier
{
    private static readonly Action NoOpScroll = () => { };

    private readonly SessionTurnCoordinator _sessionTurns;
    private readonly SessionUiCache _uiCache;
    private readonly IFileStorageService _storage;
    private readonly Lazy<ISubAgentSessionManager> _sessionManager;
    private readonly ConcurrentDictionary<string, byte> _pendingAfterTurn = new(StringComparer.Ordinal);

    public SubAgentCompletionContinuationService(
        SessionTurnCoordinator sessionTurns,
        SessionUiCache uiCache,
        IFileStorageService storage,
        Lazy<ISubAgentSessionManager> sessionManager)
    {
        _sessionTurns = sessionTurns;
        _uiCache = uiCache;
        _storage = storage;
        _sessionManager = sessionManager;
        _sessionTurns.TurnHost.TurnCompleted += OnTurnCompleted;
    }

    public void NotifyCompletionReady(string parentSessionId)
    {
        if (string.IsNullOrWhiteSpace(parentSessionId))
        {
            return;
        }

        _ = ScheduleAutoContinueAsync(parentSessionId);
    }

    private void OnTurnCompleted(object? sender, SessionTurnCompletedEventArgs e)
    {
        if (e.IsAutoContinue)
        {
            return;
        }

        if (_pendingAfterTurn.TryRemove(e.SessionId, out _))
        {
            _ = ScheduleAutoContinueAsync(e.SessionId);
            return;
        }

        _ = ScheduleAutoContinueAsync(e.SessionId);
    }

    private async Task ScheduleAutoContinueAsync(string parentSessionId)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        await dispatcher.InvokeAsync(async () => await TryStartAutoContinueAsync(parentSessionId).ConfigureAwait(true))
            .Task.ConfigureAwait(false);
    }

    private async Task TryStartAutoContinueAsync(string parentSessionId)
    {
        if (_sessionTurns.IsRunning(parentSessionId))
        {
            _pendingAfterTurn.TryAdd(parentSessionId, 0);
            return;
        }

        var pendingCount = await _sessionManager.Value
            .PeekPendingCompletionsCountAsync(parentSessionId)
            .ConfigureAwait(true);
        if (pendingCount <= 0)
        {
            return;
        }

        var session = await _storage.LoadSessionAsync(parentSessionId).ConfigureAwait(true);
        if (session is null)
        {
            return;
        }

        var ui = _uiCache.GetOrCreate(parentSessionId, NoOpScroll, NoOpScroll);
        var request = new SessionTurnRequest(
            parentSessionId,
            session,
            SubAgentAutoContinuePrompt.BuildUserMessage(),
            Array.Empty<ImageAttachment>(),
            ui,
            IsAutoContinue: true);

        if (_sessionTurns.TurnHost.TryStart(request, out _))
        {
            try
            {
                BehaviorEventManager.Instance.Record(
                    BehaviorEventIds.Subagent,
                    BehaviorEventTypes.Event,
                    BehaviorEventIds.Subagent,
                    new Dictionary<string, object?>
                    {
                        ["action"] = "auto_continue",
                        ["session_id"] = parentSessionId
                    });
            }
            catch
            {
                // ignore
            }

            _pendingAfterTurn.TryRemove(parentSessionId, out _);
            return;
        }

        _pendingAfterTurn.TryAdd(parentSessionId, 0);
    }
}
