using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public static class ModelMessagesForApiBuilder
{
    public static RequestHistoryHygiene.ApplyResult Build(
        ModelMessageCache? cache,
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        ContextCompactionSettings compaction,
        string? runtimeContext = null)
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

        if (string.IsNullOrWhiteSpace(runtimeContext))
        {
            return result;
        }

        var messagesWithRuntimeContext = result.Messages.ToList();
        messagesWithRuntimeContext.Add(new AgentModelMessage("user", runtimeContext));
        return new RequestHistoryHygiene.ApplyResult(messagesWithRuntimeContext, result.EstimatedSavingsTokens);
    }
}
