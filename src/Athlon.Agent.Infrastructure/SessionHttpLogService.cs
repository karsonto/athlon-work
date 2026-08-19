using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.SubAgents;

namespace Athlon.Agent.Infrastructure;

public sealed class ActiveAgentSessionContext : IActiveAgentSessionContext
{
    private static readonly AsyncLocal<string?> AmbientSessionId = new();

    public string? SessionId => AmbientSessionId.Value;

    public void SetSession(string? sessionId) => AmbientSessionId.Value = sessionId;

    public IDisposable Enter(string sessionId)
    {
        var previous = AmbientSessionId.Value;
        AmbientSessionId.Value = sessionId;
        return new SessionScope(previous);
    }

    private sealed class SessionScope(string? previous) : IDisposable
    {
        public void Dispose() => AmbientSessionId.Value = previous;
    }
}

public sealed class SessionHttpLogService(
    IAppPathProvider paths,
    IJsonFileStore jsonFileStore,
    IAgentRunContextAccessor runContextAccessor,
    IAppLogger logger) : ISessionHttpLogService
{
    private readonly IAppLogger _logger = logger.ForContext("SessionHttpLog");
    private readonly IAppPathProvider _ = paths;
    private readonly IJsonFileStore __ = jsonFileStore;
    private readonly IAgentRunContextAccessor ___ = runContextAccessor;

    public async Task LogInteractionAsync(string? sessionId, SessionHttpInteractionLog entry, CancellationToken cancellationToken = default)
    {
        _logger.Debug(
            "Legacy HTTP interaction log disabled ({Purpose}, status={StatusCode}, duration={DurationMs}ms)",
            entry.Purpose,
            entry.StatusCode?.ToString() ?? "n/a",
            entry.DurationMs);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

internal static class HttpLogSanitizer
{
    private const int MaxBodyChars = 120_000;

    public static string? Truncate(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= MaxBodyChars
            ? value
            : value[..MaxBodyChars] + $"\n... [truncated, total {value.Length} chars]";

    public static string RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = SensitiveText.Redact(text);
        return System.Text.RegularExpressions.Regex.Replace(
            redacted,
            @"Bearer\s+[A-Za-z0-9\-._~+/]+=*",
            "Bearer [redacted]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public static string SerializeForLog(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonFileStore.Options);
        return RedactSecrets(json);
    }
}
