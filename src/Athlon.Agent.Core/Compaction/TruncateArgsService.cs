using System.Text.Json;
using System.Text.Json.Nodes;

namespace Athlon.Agent.Core.Compaction;

public sealed class TruncateArgsService
{
    public AgentSession ApplyIfNeeded(AgentSession session, ContextCompactionSettings settings)
    {
        var updated = ApplyToMessages(session.Messages, settings, out var changed);
        return changed ? session with { Messages = updated } : session;
    }

    public IReadOnlyList<ChatMessage> ApplyToMessages(
        IReadOnlyList<ChatMessage> messages,
        ContextCompactionSettings settings,
        out bool changed)
    {
        changed = false;
        var truncateSettings = settings.TruncateArgs;
        if (!truncateSettings.Enabled || messages.Count == 0)
        {
            return messages;
        }

        var conversation = messages.Where(message => message.Role != MessageRole.Compaction).ToList();
        if (conversation.Count == 0)
        {
            return messages;
        }

        var estimatedTokens = ContextTokenEstimator.Estimate(conversation);
        if (!ConversationCutoffPlanner.ShouldTruncateArgs(conversation, estimatedTokens, truncateSettings))
        {
            return messages;
        }

        var cutoff = ConversationCutoffPlanner.DetermineTruncateArgsCutoff(conversation, truncateSettings);
        if (cutoff >= conversation.Count)
        {
            return messages;
        }

        var updatedConversation = new List<ChatMessage>(conversation.Count);
        for (var i = 0; i < conversation.Count; i++)
        {
            var message = conversation[i];
            if (i < cutoff
                && message.Role == MessageRole.Assistant
                && !string.IsNullOrWhiteSpace(message.ToolCallsJson))
            {
                var truncatedJson = TruncateToolCallsJson(
                    message.ToolCallsJson,
                    truncateSettings.MaxArgLength,
                    truncateSettings.TruncationText);

                if (!string.Equals(truncatedJson, message.ToolCallsJson, StringComparison.Ordinal))
                {
                    message = message with { ToolCallsJson = truncatedJson };
                    changed = true;
                }
            }

            updatedConversation.Add(message);
        }

        if (!changed)
        {
            return messages;
        }

        return MergeCompactionAudits(messages, updatedConversation);
    }

    internal static IReadOnlyList<ChatMessage> MergeCompactionAudits(
        IReadOnlyList<ChatMessage> original,
        IReadOnlyList<ChatMessage> conversation)
    {
        var audits = original.Where(message => message.Role == MessageRole.Compaction).ToList();
        return audits.Count == 0 ? conversation : audits.Concat(conversation).ToList();
    }

    internal static string TruncateToolCallsJson(
        string toolCallsJson,
        int maxArgLength,
        string truncationText)
    {
        var calls = AssistantToolCallsCodec.Deserialize(toolCallsJson);
        if (calls is null or { Count: 0 })
        {
            return toolCallsJson;
        }

        var changed = false;
        var updatedCalls = new List<AgentToolCall>(calls.Count);

        foreach (var call in calls)
        {
            var truncatedArgs = TruncateArguments(call.Arguments, maxArgLength, truncationText);
            if (!ReferenceEquals(truncatedArgs, call.Arguments)
                && !ArgumentsEqual(truncatedArgs, call.Arguments))
            {
                changed = true;
            }

            updatedCalls.Add(new AgentToolCall(call.Id, call.Name, truncatedArgs));
        }

        return changed
            ? AssistantToolCallsCodec.Serialize(updatedCalls) ?? toolCallsJson
            : toolCallsJson;
    }

    private static IReadOnlyDictionary<string, string> TruncateArguments(
        IReadOnlyDictionary<string, string> arguments,
        int maxArgLength,
        string truncationText)
    {
        if (arguments.Count == 0)
        {
            return arguments;
        }

        var changed = false;
        var updated = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var argument in arguments)
        {
            var truncated = TruncateArgumentValue(argument.Value, maxArgLength, truncationText);
            if (!string.Equals(truncated, argument.Value, StringComparison.Ordinal))
            {
                changed = true;
            }

            updated[argument.Key] = truncated;
        }

        return changed ? updated : arguments;
    }

    private static string TruncateArgumentValue(string value, int maxArgLength, string truncationText)
    {
        if (value.Length <= maxArgLength)
        {
            return value;
        }

        if (value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('['))
        {
            try
            {
                var node = JsonNode.Parse(value);
                if (node is JsonObject obj && TruncateJsonObject(obj, maxArgLength, truncationText))
                {
                    return obj.ToJsonString();
                }
            }
            catch (JsonException)
            {
            }
        }

        return TruncateStringArg(value, maxArgLength, truncationText);
    }

    private static bool TruncateJsonObject(JsonObject obj, int maxArgLength, string truncationText)
    {
        var changed = false;

        foreach (var key in obj.Select(property => property.Key).ToList())
        {
            var value = obj[key];
            if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            {
                var truncated = TruncateStringArg(text, maxArgLength, truncationText);
                if (!string.Equals(truncated, text, StringComparison.Ordinal))
                {
                    obj[key] = truncated;
                    changed = true;
                }
            }
            else if (value is JsonObject nested && TruncateJsonObject(nested, maxArgLength, truncationText))
            {
                changed = true;
            }
        }

        return changed;
    }

    private static string TruncateStringArg(string value, int maxArgLength, string truncationText)
    {
        if (value.Length <= maxArgLength)
        {
            return value;
        }

        var prefixLength = Math.Min(20, value.Length);
        return value[..prefixLength] + truncationText;
    }

    private static bool ArgumentsEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value)
                || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
