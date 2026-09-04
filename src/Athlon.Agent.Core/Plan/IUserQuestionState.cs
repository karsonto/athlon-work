namespace Athlon.Agent.Core.Plan;

public sealed class UserQuestionChangedEventArgs(
    string sessionId,
    UserQuestion? question) : EventArgs
{
    public string SessionId { get; } = sessionId;

    /// <summary>The pending question for the session, or null when it was cleared.</summary>
    public UserQuestion? Question { get; } = question?.Clone();
}

/// <summary>
/// Process-wide, in-memory holder of pending <c>ask_user</c> questions, keyed by
/// session id. Never persisted: a pending question is lost on restart, by design.
/// </summary>
public interface IUserQuestionState
{
    event EventHandler<UserQuestionChangedEventArgs>? QuestionChanged;

    UserQuestion? GetPending(string sessionId);

    void SetPending(string sessionId, UserQuestion question);

    void Clear(string sessionId);
}
