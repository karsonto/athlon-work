using System.Text.Json;

namespace Athlon.Agent.Core.Compaction;

public static class ContextTokenEstimator
{
    public static int Estimate(IReadOnlyList<ChatMessage> messages)
    {
        var relevant = messages.Where(message => message.Role != MessageRole.Compaction).ToArray();
        return Math.Max(0, JsonSerializer.Serialize(relevant).Length / 4);
    }
}
