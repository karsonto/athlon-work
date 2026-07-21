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
        out bool changed,
        int? keepTokenBudgetOverride = null)
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

        var estimatedTokens = ContextTokenEstimator.Estimate(conversation, settings.IncludeReasoningInModelContext);
        if (keepTokenBudgetOverride is null
            && !ConversationCutoffPlanner.ShouldTruncateArgs(conversation, estimatedTokens, truncateSettings))
        {
            return messages;
        }

        // Dynamic plan passes keepTokenBudgetOverride (including 0). Zero means truncate all
        // assistant tool args in history, without falling back to the static message/token gate.
        int cutoff;
        if (keepTokenBudgetOverride is null)
        {
            cutoff = ConversationCutoffPlanner.DetermineTruncateArgsCutoff(
                conversation,
                truncateSettings,
                settings.IncludeReasoningInModelContext);
            if (cutoff >= conversation.Count)
            {
                return messages;
            }
        }
        else if (keepTokenBudgetOverride.Value <= 0)
        {
            cutoff = conversation.Count;
        }
        else
        {
            cutoff = ConversationCutoffPlanner.DetermineTruncateArgsCutoffFromKeepBudget(
                conversation,
                keepTokenBudgetOverride.Value,
                settings.IncludeReasoningInModelContext);
            if (cutoff >= conversation.Count)
            {
                return messages;
            }
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

    private static ToolCallArguments TruncateArguments(
        ToolCallArguments arguments,
        int maxArgLength,
        string truncationText)
    {
        if (arguments.Count == 0)
        {
            return arguments;
        }

        var changed = false;
        var updated = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var argument in arguments)
        {
            var truncated = TruncateArgumentValue(argument.Value, maxArgLength, truncationText);
            if (!string.Equals(truncated.GetRawText(), argument.Value.GetRawText(), StringComparison.Ordinal))
            {
                changed = true;
            }

            updated[argument.Key] = truncated;
        }

        return changed ? new ToolCallArguments(updated) : arguments;
    }

    private static JsonElement TruncateArgumentValue(
        JsonElement value,
        int maxArgLength,
        string truncationText)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            return JsonSerializer.SerializeToElement(TruncateStringArg(text, maxArgLength, truncationText));
        }

        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            try
            {
                var node = JsonNode.Parse(value.GetRawText());
                if (node is not null && TruncateJsonNode(node, maxArgLength, truncationText))
                {
                    return JsonSerializer.SerializeToElement(node);
                }
            }
            catch (JsonException)
            {
            }
        }

        return value.Clone();
    }

    private static bool TruncateJsonNode(JsonNode node, int maxArgLength, string truncationText)
    {
        var changed = false;
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(property => property.Key).ToList())
            {
                var child = obj[key];
                if (child is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
                {
                    var truncated = TruncateStringArg(text, maxArgLength, truncationText);
                    if (!string.Equals(truncated, text, StringComparison.Ordinal))
                    {
                        obj[key] = truncated;
                        changed = true;
                    }
                }
                else if (child is not null && TruncateJsonNode(child, maxArgLength, truncationText))
                {
                    changed = true;
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var child = array[index];
                if (child is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
                {
                    var truncated = TruncateStringArg(text, maxArgLength, truncationText);
                    if (!string.Equals(truncated, text, StringComparison.Ordinal))
                    {
                        array[index] = truncated;
                        changed = true;
                    }
                }
                else if (child is not null && TruncateJsonNode(child, maxArgLength, truncationText))
                {
                    changed = true;
                }
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
        ToolCallArguments left,
        ToolCallArguments right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value)
                || !string.Equals(value.GetRawText(), pair.Value.GetRawText(), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
