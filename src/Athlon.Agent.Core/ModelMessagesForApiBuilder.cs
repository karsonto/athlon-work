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
        List<AgentModelMessage> messages;
        if (cache is not null)
        {
            messages = cache.Build(environmentPrompt, history, compaction.IncludeReasoningInModelContext);
        }
        else
        {
            messages = ModelMessageBuilder.BuildForSession(
                environmentPrompt,
                history,
                compaction.IncludeReasoningInModelContext);
        }

        ModelMessageBuilder.RetainLatestToolScreenshots(
            messages,
            compaction.MaxToolScreenshotsInModelContext);

        var result = cache is not null
            ? cache.ApplyHygiene(compaction.RequestHistoryHygiene)
            : RequestHistoryHygiene.ApplyToModelMessages(messages, compaction.RequestHistoryHygiene);

        if (runtimeContextState is null)
        {
            if (string.IsNullOrWhiteSpace(runtimeContext))
            {
                return result;
            }

            var withContext = result.Messages.ToList();
            withContext.Add(new AgentModelMessage("user", runtimeContext));
            return new RequestHistoryHygiene.ApplyResult(withContext, result.EstimatedSavingsTokens);
        }

        var injection = runtimeContextState.SelectForInjection(runtimeContext);
        if (injection.Messages.Count == 0)
        {
            return result;
        }

        var messagesWithRuntimeContext = result.Messages.ToList();
        messagesWithRuntimeContext.AddRange(injection.Messages);
        return new RequestHistoryHygiene.ApplyResult(messagesWithRuntimeContext, result.EstimatedSavingsTokens);
    }
}

public sealed class RuntimeContextInjectionState
{
    private string? _lastFingerprint;
    private string? _previousContext;

    public string? LastSelectedContext { get; private set; }

    public bool FingerprintChanged { get; private set; }

    public RuntimeContextInjection SelectForInjection(string? runtimeContext)
    {
        var fingerprint = RuntimeContextSnapshot.ComputeFingerprint(runtimeContext);
        FingerprintChanged = !string.Equals(_lastFingerprint, fingerprint, StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(runtimeContext))
        {
            _lastFingerprint = fingerprint;
            LastSelectedContext = null;
            _previousContext = null;
            return RuntimeContextInjection.Empty;
        }

        List<AgentModelMessage> messages;
        if (FingerprintChanged && !string.IsNullOrWhiteSpace(_previousContext))
        {
            var superseded = _previousContext
                + Environment.NewLine
                + Environment.NewLine
                + "(superseded by newer runtime context)";
            messages =
            [
                new AgentModelMessage("user", superseded),
                new AgentModelMessage("user", runtimeContext)
            ];
        }
        else
        {
            messages = [new AgentModelMessage("user", runtimeContext)];
        }

        _previousContext = runtimeContext;
        _lastFingerprint = fingerprint;
        LastSelectedContext = runtimeContext;
        return new RuntimeContextInjection(messages);
    }
}

public sealed record RuntimeContextInjection(IReadOnlyList<AgentModelMessage> Messages)
{
    public static RuntimeContextInjection Empty { get; } = new([]);
}
