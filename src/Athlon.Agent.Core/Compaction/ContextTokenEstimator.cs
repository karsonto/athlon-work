using System.Text.Json;

namespace Athlon.Agent.Core.Compaction;

public static class ContextTokenEstimator
{
    public static int Estimate(IReadOnlyList<ChatMessage> messages) =>
        Math.Max(0, JsonSerializer.Serialize(messages).Length / 4);
}
