namespace Athlon.Agent.Core;

/// <summary>
/// Incrementally builds model messages within a turn; invalidates when compaction reshapes history.
/// </summary>
internal sealed class ModelMessageCache
{
    private List<AgentModelMessage>? _messages;
    private string? _environmentPrompt;
    private bool _includeReasoning;
    private int _processedHistoryCount;

    public void Invalidate()
    {
        _messages = null;
        _environmentPrompt = null;
        _processedHistoryCount = 0;
    }

    public void NotePreCompletionResult(IReadOnlyList<ChatMessage> historyBefore, IReadOnlyList<ChatMessage> historyAfter)
    {
        if (historyAfter.Count < historyBefore.Count)
        {
            Invalidate();
            return;
        }

        var compareCount = Math.Min(_processedHistoryCount, Math.Min(historyBefore.Count, historyAfter.Count));
        for (var index = 0; index < compareCount; index++)
        {
            if (!string.Equals(historyBefore[index].Id, historyAfter[index].Id, StringComparison.Ordinal))
            {
                Invalidate();
                return;
            }
        }
    }

    public List<AgentModelMessage> Build(
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        bool includeReasoningInModelContext)
    {
        if (_messages is not null
            && string.Equals(_environmentPrompt, environmentPrompt, StringComparison.Ordinal)
            && _includeReasoning == includeReasoningInModelContext
            && history.Count >= _processedHistoryCount)
        {
            if (history.Count == _processedHistoryCount)
            {
                return _messages;
            }

            for (var index = _processedHistoryCount; index < history.Count; index++)
            {
                index = ModelMessageBuilder.AppendHistoryMessage(
                    _messages,
                    history,
                    index,
                    includeReasoningInModelContext);
            }

            _processedHistoryCount = history.Count;
            return _messages;
        }

        _messages = ModelMessageBuilder.BuildForSession(environmentPrompt, history, includeReasoningInModelContext);
        _environmentPrompt = environmentPrompt;
        _includeReasoning = includeReasoningInModelContext;
        _processedHistoryCount = history.Count;
        return _messages;
    }
}
