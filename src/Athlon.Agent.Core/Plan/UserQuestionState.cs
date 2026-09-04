using System.Collections.Concurrent;

namespace Athlon.Agent.Core.Plan;

/// <summary>
/// Default <see cref="IUserQuestionState"/>: a per-session dictionary of pending
/// <c>ask_user</c> questions held only in process memory.
/// </summary>
public sealed class UserQuestionState : IUserQuestionState
{
    private readonly ConcurrentDictionary<string, UserQuestion> _pending = new(StringComparer.Ordinal);

    public event EventHandler<UserQuestionChangedEventArgs>? QuestionChanged;

    public UserQuestion? GetPending(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_pending.TryGetValue(sessionId, out var question))
        {
            return null;
        }

        return question.Clone();
    }

    public void SetPending(string sessionId, UserQuestion question)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || question is null)
        {
            return;
        }

        _pending[sessionId] = question.Clone();
        QuestionChanged?.Invoke(this, new UserQuestionChangedEventArgs(sessionId, question));
    }

    public void Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_pending.TryRemove(sessionId, out _))
        {
            return;
        }

        QuestionChanged?.Invoke(this, new UserQuestionChangedEventArgs(sessionId, null));
    }
}
