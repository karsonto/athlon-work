using Athlon.Agent.Core.Compaction;

namespace Athlon.Agent.Core;

public static class ModelMessagesForApiBuilder
{
    public static RequestHistoryHygiene.ApplyResult Build(
        ModelMessageCache? cache,
        string environmentPrompt,
        IReadOnlyList<ChatMessage> history,
        ContextCompactionSettings compaction)
    {
        if (cache is not null)
        {
            cache.Build(environmentPrompt, history, compaction.IncludeReasoningInModelContext);
            return cache.ApplyHygiene(compaction.RequestHistoryHygiene);
        }

        var messages = ModelMessageBuilder.BuildForSession(environmentPrompt, history, compaction.IncludeReasoningInModelContext);
        return RequestHistoryHygiene.ApplyToModelMessages(messages, compaction.RequestHistoryHygiene);
    }
}
