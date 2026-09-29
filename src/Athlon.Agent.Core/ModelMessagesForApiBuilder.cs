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
        RuntimeContextInjectionState? runtimeContextState = null,
        string? tokenBudgetNotice = null)
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

        var hygieneResult = cache is not null
            ? cache.ApplyHygiene(compaction.RequestHistoryHygiene)
            : RequestHistoryHygiene.ApplyToModelMessages(messages, compaction.RequestHistoryHygiene);

        RequestHistoryHygiene.ApplyResult result;
        if (runtimeContextState is null)
        {
            if (string.IsNullOrWhiteSpace(runtimeContext))
            {
                result = hygieneResult;
            }
            else
            {
                var withContext = hygieneResult.Messages.ToList();
                withContext.Add(new AgentModelMessage("user", runtimeContext));
                result = new RequestHistoryHygiene.ApplyResult(withContext, hygieneResult.EstimatedSavingsTokens);
            }
        }
        else
        {
            var injection = runtimeContextState.SelectForInjection(runtimeContext);
            if (injection.Messages.Count == 0)
            {
                result = hygieneResult;
            }
            else
            {
                var messagesWithRuntimeContext = hygieneResult.Messages.ToList();
                messagesWithRuntimeContext.AddRange(injection.Messages);
                result = new RequestHistoryHygiene.ApplyResult(
                    messagesWithRuntimeContext,
                    hygieneResult.EstimatedSavingsTokens);
            }
        }

        if (string.IsNullOrWhiteSpace(tokenBudgetNotice))
        {
            return result;
        }

        var withNotice = result.Messages.ToList();
        withNotice.Add(new AgentModelMessage("user", tokenBudgetNotice));
        return new RequestHistoryHygiene.ApplyResult(withNotice, result.EstimatedSavingsTokens);
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
            messages =
            [
                new AgentModelMessage("user", "Runtime context updated."),
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
