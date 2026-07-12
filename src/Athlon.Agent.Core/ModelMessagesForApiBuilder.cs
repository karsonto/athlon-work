using Athlon.Agent.Core.Compaction;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Core;

public static class ModelMessagesForApiBuilder
{
    public static RequestHistoryHygiene.ApplyResult Build(
        ModelMessageCache? cache,
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        ContextCompactionSettings compaction,
        string? runtimeContext = null,
        RuntimeContextInjectionState? runtimeContextState = null)
    {
        RequestHistoryHygiene.ApplyResult result;
        if (cache is not null)
        {
            cache.Build(environmentPrompt, history, compaction.IncludeReasoningInModelContext);
            result = cache.ApplyHygiene(compaction.RequestHistoryHygiene);
        }
        else
        {
            var messages = ModelMessageBuilder.BuildForSession(environmentPrompt, history, compaction.IncludeReasoningInModelContext);
            result = RequestHistoryHygiene.ApplyToModelMessages(messages, compaction.RequestHistoryHygiene);
        }

        var contextToInject = runtimeContextState is null
            ? runtimeContext
            : runtimeContextState.SelectForInjection(runtimeContext);
        if (string.IsNullOrWhiteSpace(contextToInject))
        {
            return result;
        }

        var messagesWithRuntimeContext = result.Messages.ToList();
        messagesWithRuntimeContext.Add(new AgentModelMessage("user", contextToInject));
        return new RequestHistoryHygiene.ApplyResult(messagesWithRuntimeContext, result.EstimatedSavingsTokens);
    }
}

public sealed class RuntimeContextInjectionState
{
    private string? _lastFingerprint;

    public string? LastSelectedContext { get; private set; }
    public bool FingerprintChanged { get; private set; }

    public string? SelectForInjection(string? runtimeContext)
    {
        var fingerprint = RuntimeContextSnapshot.ComputeFingerprint(runtimeContext);
        FingerprintChanged = !string.Equals(_lastFingerprint, fingerprint, StringComparison.Ordinal);
        _lastFingerprint = fingerprint;
        LastSelectedContext = string.IsNullOrWhiteSpace(runtimeContext) ? null : runtimeContext;
        return LastSelectedContext;
    }
}
