using System.Text.Json;
using Athlon.Agent.Core.Chat;

namespace Athlon.Agent.Infrastructure.Chat;

/// <summary>
/// OpenAI-compatible messages whose answer embeds thinking between provider-specific tags
/// (e.g. redacted_thinking / think). Prefer this over <see cref="OpenAiCompatibleDialect"/>
/// when tags are present and no dedicated reasoning field was supplied.
/// </summary>
internal sealed class EmbeddedThinkingDialect : IChatResponseDialect
{
    private static readonly string[] EndTags =
    [
        "\u003c/redacted_thinking\u003e",
        "\u0060/think\u0060"
    ];

    private static readonly string[] StartTags =
    [
        "\u003credacted_thinking\u003e",
        "\u0060think\u0060"
    ];

    private readonly OpenAiCompatibleDialect _inner = new();

    public string Name => "embedded-thinking";

    public int Priority => 100;

    public bool CanHandle(JsonElement message)
    {
        if (!_inner.CanHandle(message))
        {
            return false;
        }

        // Dedicated reasoning field wins; leave to the plain OpenAI dialect.
        if (!string.IsNullOrWhiteSpace(_inner.ReadReasoning(message)))
        {
            return false;
        }

        var content = _inner.ReadContent(message);
        return ContainsEndTag(content);
    }

    public string ReadContent(JsonElement message) => _inner.ReadContent(message);

    public string? ReadReasoning(JsonElement message) => _inner.ReadReasoning(message);

    public IEnumerable<ChatToolCallDraft> ReadToolCalls(JsonElement message) =>
        _inner.ReadToolCalls(message);

    public (string Content, string? Reasoning) Normalize(string content, string? reasoning)
    {
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            return (content, reasoning);
        }

        foreach (var endTag in EndTags)
        {
            var index = content.IndexOf(endTag, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var thinking = content[..index].Trim();
            var answer = content[(index + endTag.Length)..].Trim();
            foreach (var startTag in StartTags)
            {
                if (thinking.StartsWith(startTag, StringComparison.OrdinalIgnoreCase))
                {
                    thinking = thinking[startTag.Length..].Trim();
                    break;
                }
            }

            return (answer, string.IsNullOrWhiteSpace(thinking) ? null : thinking);
        }

        return (content, null);
    }

    public string? ReadReasoningDelta(JsonElement payload) => _inner.ReadReasoningDelta(payload);

    private static bool ContainsEndTag(string content)
    {
        foreach (var endTag in EndTags)
        {
            if (content.IndexOf(endTag, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
