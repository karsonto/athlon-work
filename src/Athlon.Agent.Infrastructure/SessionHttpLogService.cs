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

public sealed class SessionHttpLogService(IAppPathProvider paths, IJsonFileStore jsonFileStore, IAppLogger logger) : ISessionHttpLogService
{
    private readonly IAppLogger _logger = logger.ForContext("SessionHttpLog");

    public async Task LogInteractionAsync(string? sessionId, SessionHttpInteractionLog entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _logger.Debug("Skipped HTTP log without session id ({Purpose})", entry.Purpose);
            return;
        }

        var sessionDir = AmbientSubAgentStorageScope.ResolveSessionDirectory(paths.SessionsPath, sessionId);
        var httpDir = Path.Combine(sessionDir, "http");
        Directory.CreateDirectory(httpDir);
        var path = Path.Combine(httpDir, "interactions.jsonl");

        var record = new
        {
            time = entry.Timestamp,
            endpoint = entry.Endpoint,
            purpose = entry.Purpose,
            statusCode = entry.StatusCode,
            durationMs = entry.DurationMs,
            request = entry.Request is null ? null : HttpLogSanitizer.SerializeForLog(entry.Request),
            responseBody = entry.ResponseBody is null
                ? null
                : HttpLogSanitizer.Truncate(HttpLogSanitizer.RedactSecrets(entry.ResponseBody)),
            error = entry.Error
        };

        using (await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            await jsonFileStore.AppendJsonLineAsync(path, record, cancellationToken, prettyPrint: true);
        }

        _logger.Information(
            "HTTP {Purpose} logged for session {SessionId} status={StatusCode} duration={DurationMs}ms",
            entry.Purpose,
            sessionId,
            entry.StatusCode,
            entry.DurationMs);
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
