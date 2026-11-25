using System.Text.Json;

namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Rewrites assistant <c>ToolCallsJson</c> in place so huge string arguments shrink before
/// utilization is re-measured for pre-completion compaction.
/// </summary>
public static class TruncateArgsService
{
    public const string TruncationSuffix = "...(truncated)";

    public sealed record ApplyResult(IReadOnlyList<ChatMessage> Messages, bool Changed);

    public static ApplyResult Apply(
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings settings)
    {
        var truncate = settings.TruncateArgs;
        if (!truncate.Enabled || messages.Count == 0)
        {
            return new ApplyResult(messages, false);
        }

        var maxLength = Math.Max(64, truncate.MaxArgLength);
        var changed = false;
        var output = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (message.Role != MessageRole.Assistant
                || string.IsNullOrWhiteSpace(message.ToolCallsJson))
            {
                output.Add(message);
                continue;
            }

            var calls = AssistantToolCallsCodec.Deserialize(message.ToolCallsJson);
            if (calls is not { Count: > 0 })
            {
                output.Add(message);
                continue;
            }

            var rewritten = new List<AgentToolCall>(calls.Count);
            var callChanged = false;
            foreach (var call in calls)
            {
                var compacted = CompactCallArguments(call, maxLength);
                if (!ReferenceEquals(compacted, call))
                {
                    callChanged = true;
                }

                rewritten.Add(compacted);
            }

            if (!callChanged)
            {
                output.Add(message);
                continue;
            }

            changed = true;
            output.Add(message with { ToolCallsJson = AssistantToolCallsCodec.Serialize(rewritten) });
        }

        return changed ? new ApplyResult(output, true) : new ApplyResult(messages, false);
    }

    private static AgentToolCall CompactCallArguments(AgentToolCall call, int maxLength)
    {
        if (call.Arguments.Count == 0)
        {
            return call;
        }

        var changed = false;
        var output = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in call.Arguments)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                output[key] = value;
                continue;
            }

            var text = value.GetString() ?? string.Empty;
            if (text.Length <= maxLength)
            {
                output[key] = value;
                continue;
            }

            changed = true;
            var preview = text[..Math.Min(text.Length, Math.Max(16, maxLength - TruncationSuffix.Length))];
            output[key] = JsonSerializer.SerializeToElement(preview + TruncationSuffix);
        }

        return changed ? new AgentToolCall(call.Id, call.Name, new ToolCallArguments(output)) : call;
    }
}