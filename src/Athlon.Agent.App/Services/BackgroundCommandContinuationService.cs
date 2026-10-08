using System.Collections.Concurrent;
using System.Windows;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services;

/// <summary>
/// When a background command finishes after the turn has ended, starts an auto-continue turn
/// so the completion is drained into runtime context without another user message.
/// </summary>
public sealed class BackgroundCommandContinuationService : IBackgroundCommandCompletionNotifier
{
    private static readonly Action NoOpScroll = () => { };

    private readonly SessionTurnCoordinator _sessionTurns;
    private readonly SessionUiCache _uiCache;
    private readonly IFileStorageService _storage;
    private readonly BackgroundCommandRegistry _commands;
    private readonly ConcurrentDictionary<string, byte> _pendingAfterTurn = new(StringComparer.Ordinal);

    public BackgroundCommandContinuationService(
        SessionTurnCoordinator sessionTurns,
        SessionUiCache uiCache,
        IFileStorageService storage,
        BackgroundCommandRegistry commands)
    {
        _sessionTurns = sessionTurns;
        _uiCache = uiCache;
        _storage = storage;
        _commands = commands;
        _sessionTurns.TurnHost.TurnCompleted += OnTurnCompleted;
    }

    public void NotifyCompletionReady(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _ = ScheduleAutoContinueAsync(sessionId);
    }

    private void OnTurnCompleted(object? sender, SessionTurnCompletedEventArgs e)
    {
        if (e.Cancelled)
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

    private async Task ScheduleAutoContinueAsync(string sessionId)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        await dispatcher.InvokeAsync(async () => await TryStartAutoContinueAsync(sessionId).ConfigureAwait(true))
            .Task.ConfigureAwait(false);
    }

    private async Task TryStartAutoContinueAsync(string sessionId)
    {
        if (_sessionTurns.IsRunning(sessionId))
        {
            _pendingAfterTurn.TryAdd(sessionId, 0);
            return;
        }

        if (_commands.PeekCompletionCount(sessionId) <= 0)
        {
            return;
        }

        var session = await _storage.LoadSessionAsync(sessionId).ConfigureAwait(true);
        if (session is null)
        {
            return;
        }

        var ui = _uiCache.GetOrCreate(sessionId, NoOpScroll, NoOpScroll);
        var request = new SessionTurnRequest(
            sessionId,
            session,
            BackgroundCommandAutoContinuePrompt.BuildUserMessage(),
            Array.Empty<ImageAttachment>(),
            ui,
            IsAutoContinue: true);

        if (_sessionTurns.TurnHost.TryStart(request, out _))
        {
            _pendingAfterTurn.TryRemove(sessionId, out _);
            return;
        }

        _pendingAfterTurn.TryAdd(sessionId, 0);
    }
}
