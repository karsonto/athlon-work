using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

/// <summary>
/// Incrementally builds model messages within a turn; invalidates when compaction reshapes history.
/// </summary>
public sealed class ModelMessageCache
{
    private List<AgentModelMessage>? _messages;
    private string? _environmentPrompt;
    private bool _includeReasoning;
    private int _processedHistoryCount;
    private List<AgentModelMessage>? _hygienizedPrefix;
    private int _hygienizedMessageCount;
    private int _hygienizedSavings;
    private string? _rawPrefixFingerprint;

    public void Invalidate()
    {
        _messages = null;
        _environmentPrompt = null;
        _processedHistoryCount = 0;
        _hygienizedPrefix = null;
        _hygienizedMessageCount = 0;
        _hygienizedSavings = 0;
        _rawPrefixFingerprint = null;
    }

    public RequestHistoryHygiene.ApplyResult ApplyHygiene(
        RequestHistoryHygieneSettings settings)
    {
        if (_messages is null)
        {
            return new RequestHistoryHygiene.ApplyResult(Array.Empty<AgentModelMessage>(), 0);
        }

        var prefixFingerprint = ComputeFingerprint(_messages, _hygienizedMessageCount);
        if (_hygienizedPrefix is not null
            && _hygienizedMessageCount > 0
            && _hygienizedMessageCount < _messages.Count
            && string.Equals(prefixFingerprint, _rawPrefixFingerprint, StringComparison.Ordinal))
        {
            var tail = _messages.Skip(_hygienizedMessageCount).ToList();
            var tailResult = RequestHistoryHygiene.ApplyToModelMessages(tail, settings);
            var merged = new List<AgentModelMessage>(_hygienizedPrefix);
            merged.AddRange(tailResult.Messages);
            _hygienizedPrefix = merged;
            _hygienizedMessageCount = _messages.Count;
            _hygienizedSavings += tailResult.EstimatedSavingsTokens;
            _rawPrefixFingerprint = ComputeFingerprint(_messages, _hygienizedMessageCount);
            return new RequestHistoryHygiene.ApplyResult(merged, _hygienizedSavings);
        }

        var full = RequestHistoryHygiene.ApplyToModelMessages(_messages, settings);
        _hygienizedPrefix = full.Messages.ToList();
        _hygienizedMessageCount = _messages.Count;
        _hygienizedSavings = full.EstimatedSavingsTokens;
        _rawPrefixFingerprint = ComputeFingerprint(_messages, _hygienizedMessageCount);
        return full;
    }

    private static string ComputeFingerprint(IReadOnlyList<AgentModelMessage> messages, int count)
    {
        var parts = new List<string>(Math.Min(count, messages.Count));
        for (var index = 0; index < Math.Min(count, messages.Count); index++)
        {
            var message = messages[index];
            parts.Add($"{message.Role}|{GetTextContent(message.Content)}|{message.ToolCallId}");
        }

        return string.Join('\n', parts);
    }

    private static string GetTextContent(object? content) =>
        content switch
        {
            null => string.Empty,
            string text => text,
            _ => content.ToString() ?? string.Empty
        };

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
