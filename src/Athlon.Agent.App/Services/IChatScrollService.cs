namespace Athlon.Agent.App.Services;

public interface IChatScrollService
{
    void ScrollToBottom();

    void ScrollToBottomImmediate();

    void Register(Action? scrollToBottom, Action? scrollToBottomImmediate);
}

public sealed class ChatScrollService : IChatScrollService
{
    private Action? _scrollToBottom;
    private Action? _scrollToBottomImmediate;

    public void Register(Action? scrollToBottom, Action? scrollToBottomImmediate)
    {
        _scrollToBottom = scrollToBottom;
        _scrollToBottomImmediate = scrollToBottomImmediate;
    }

    public void ScrollToBottom() => _scrollToBottom?.Invoke();

    public void ScrollToBottomImmediate() => _scrollToBottomImmediate?.Invoke();
}
