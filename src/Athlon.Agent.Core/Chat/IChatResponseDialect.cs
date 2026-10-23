using System.Text.Json;

namespace Athlon.Agent.Core.Chat;

/// <summary>
/// Provider-specific strategy for reading OpenAI-compatible (or similar) assistant messages.
/// New providers add a dialect instead of growing the shared parser.
/// </summary>
public interface IChatResponseDialect
{
    string Name { get; }

    /// <summary>Higher wins when multiple dialects match.</summary>
    int Priority { get; }

    /// <summary>Whether this dialect should parse the given assistant message object.</summary>
    bool CanHandle(JsonElement message);

    string ReadContent(JsonElement message);

    string? ReadReasoning(JsonElement message);

    /// <summary>Raw tool-call drafts from the message (id, name, arguments JSON).</summary>
    IEnumerable<ChatToolCallDraft> ReadToolCalls(JsonElement message);

    /// <summary>
    /// Post-process content/reasoning (e.g. peel embedded thinking tags out of content).
    /// </summary>
    (string Content, string? Reasoning) Normalize(string content, string? reasoning);

    /// <summary>Reasoning token from a streaming delta/message payload, if present.</summary>
    string? ReadReasoningDelta(JsonElement payload);
}

/// <summary>Provider-agnostic tool-call fields before Infrastructure normalizes paths / parse errors.</summary>
public readonly record struct ChatToolCallDraft(string Id, string Name, string ArgumentsJson);
