using System.Text.Json;
using Athlon.Agent.Core.Chat;

namespace Athlon.Agent.Infrastructure.Chat;

/// <summary>Selects the highest-priority dialect that can handle a message.</summary>
internal sealed class ChatResponseDialectRegistry
{
    private readonly IReadOnlyList<IChatResponseDialect> _dialects;

    public ChatResponseDialectRegistry(IEnumerable<IChatResponseDialect> dialects)
    {
        _dialects = dialects
            .OrderByDescending(d => d.Priority)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
        if (_dialects.Count == 0)
        {
            throw new ArgumentException("At least one chat response dialect is required.", nameof(dialects));
        }
    }

    public static ChatResponseDialectRegistry CreateDefault() =>
        new([
            new EmbeddedThinkingDialect(),
            new OpenAiCompatibleDialect()
        ]);

    public IChatResponseDialect Resolve(JsonElement message)
    {
        foreach (var dialect in _dialects)
        {
            if (dialect.CanHandle(message))
            {
                return dialect;
            }
        }

        return _dialects[^1];
    }

    /// <summary>
    /// Dialect used for streaming deltas (content is incomplete; prefer OpenAI-compatible field reads).
    /// </summary>
    public IChatResponseDialect StreamingDefault =>
        _dialects.FirstOrDefault(d => d is OpenAiCompatibleDialect) ?? _dialects[^1];
}
