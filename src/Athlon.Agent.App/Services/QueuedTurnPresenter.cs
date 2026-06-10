using System.Collections.ObjectModel;
using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed class QueuedTurnPresenter(SessionTurnHost turnHost)
{
    private readonly Dictionary<string, ObservableCollection<QueuedTurnViewModel>> _bySession = new(StringComparer.Ordinal);

    public event Action<string>? QueueChanged;

    public ObservableCollection<QueuedTurnViewModel> GetForSession(string sessionId)
    {
        if (!_bySession.TryGetValue(sessionId, out var collection))
        {
            collection = new ObservableCollection<QueuedTurnViewModel>();
            _bySession[sessionId] = collection;
        }

        return collection;
    }

    public void Enqueue(
        string sessionId,
        string queueId,
        string input,
        IReadOnlyList<ImageAttachment> imageAttachments,
        SessionTurnUiController ui)
    {
        turnHost.Enqueue(new QueuedTurnPayload(queueId, sessionId, input, imageAttachments, ui));
        GetForSession(sessionId).Add(QueuedTurnViewModel.Create(queueId, input, imageAttachments));
        QueueChanged?.Invoke(sessionId);
    }

    public bool Remove(string sessionId, string queueId)
    {
        if (!turnHost.Remove(sessionId, queueId))
        {
            return false;
        }

        var collection = GetForSession(sessionId);
        var item = collection.FirstOrDefault(turn => string.Equals(turn.QueueId, queueId, StringComparison.Ordinal));
        if (item is not null)
        {
            collection.Remove(item);
        }

        QueueChanged?.Invoke(sessionId);
        return true;
    }

    public void Clear(string sessionId)
    {
        turnHost.ClearQueue(sessionId);
        GetForSession(sessionId).Clear();
        QueueChanged?.Invoke(sessionId);
    }

    public void RemoveSession(string sessionId)
    {
        _bySession.Remove(sessionId);
    }

    public bool TryProcessNext(
        SessionTurnCompletedEventArgs e,
        out string? startError)
    {
        startError = null;
        if (!turnHost.TryDequeue(e.SessionId, out var payload) || payload is null)
        {
            return false;
        }

        RemoveFromUi(e.SessionId, payload.QueueId);
        payload.Ui.AddUserMessage(payload.UserInput, payload.ImageAttachments);
        var request = new SessionTurnRequest(
            e.SessionId,
            e.Session,
            payload.UserInput,
            payload.ImageAttachments,
            payload.Ui,
            IsAutoContinue: false);

        if (turnHost.TryStart(request, out startError))
        {
            return true;
        }

        turnHost.RequeueFront(payload);
        GetForSession(e.SessionId).Insert(
            0,
            QueuedTurnViewModel.Create(payload.QueueId, payload.UserInput, payload.ImageAttachments));
        QueueChanged?.Invoke(e.SessionId);
        startError ??= "无法开始下一条排队消息。";
        return true;
    }

    private void RemoveFromUi(string sessionId, string queueId)
    {
        var collection = GetForSession(sessionId);
        var item = collection.FirstOrDefault(turn => string.Equals(turn.QueueId, queueId, StringComparison.Ordinal));
        if (item is not null)
        {
            collection.Remove(item);
        }

        QueueChanged?.Invoke(sessionId);
    }
}
