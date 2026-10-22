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
    AppSettings settings,
    IAppLogger logger) : ISessionHttpLogService
{
    private readonly IAppLogger _logger = logger.ForContext("SessionHttpLog");
    private readonly IAppPathProvider _paths = paths;
    private readonly IJsonFileStore _jsonFileStore = jsonFileStore;
    private readonly IAgentRunContextAccessor _runContextAccessor = runContextAccessor;
    private readonly LoggingSettings _logging = settings.Logging;

    public bool IsEnabled => _logging.HttpInteractionLogEnabled;

    public async Task LogInteractionAsync(string? sessionId, SessionHttpInteractionLog entry, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            var path = ResolveInteractionsPath(sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var maxChars = _logging.HttpMaxBodyChars > 0 ? _logging.HttpMaxBodyChars : HttpLogSanitizer.DefaultMaxBodyChars;
            var line = new Dictionary<string, object?>
            {
                ["ts"] = AppTimeZone.ToChina(entry.Timestamp),
                ["requestId"] = entry.RequestId,
                ["sessionId"] = sessionId,
                ["endpoint"] = entry.Endpoint,
                ["purpose"] = entry.Purpose,
                ["statusCode"] = entry.StatusCode,
                ["durationMs"] = entry.DurationMs,
                ["error"] = entry.Error,
                ["request"] = HttpLogSanitizer.ToLogPayload(entry.Request, maxChars),
                ["responseBody"] = HttpLogSanitizer.Truncate(
                    entry.ResponseBody is null ? null : HttpLogSanitizer.RedactSecrets(entry.ResponseBody),
                    maxChars),
            };

            await _jsonFileStore.AppendJsonLineAsync(path, line, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(
                "Failed to write HTTP interaction log for session {SessionId}: {Message}",
                sessionId ?? "(none)",
                ex.Message);
            throw;
        }
    }

    private string ResolveInteractionsPath(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Path.Combine(_paths.LogsPath, "http", "interactions.jsonl");
        }

        var resolved = _runContextAccessor.ResolveSessionDirectory(_paths.SessionsPath, sessionId);
        resolved = SessionDirectoryLayout.ResolveEffectiveSessionDirectory(
            _paths.SessionsPath,
            sessionId,
            resolved,
            _runContextAccessor.Current?.Kind ?? AgentRunKind.Root);

        return Path.Combine(resolved, "http", "interactions.jsonl");
    }
}

internal static class HttpLogSanitizer
{
    public const int DefaultMaxBodyChars = 120_000;

    public static string? Truncate(string? value, int maxBodyChars = DefaultMaxBodyChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxBodyChars)
        {
            return value;
        }

        return value[..maxBodyChars] + $"\n... [truncated, total {value.Length} chars]";
    }

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

    public static string SerializeForLog(object value, int maxBodyChars = DefaultMaxBodyChars)
    {
        var json = JsonSerializer.Serialize(value, JsonFileStore.Options);
        return Truncate(RedactSecrets(json), maxBodyChars) ?? string.Empty;
    }

    /// <summary>
    /// Embeds request as a JSON object when possible; falls back to a truncated string when the
    /// redacted payload exceeds <paramref name="maxBodyChars"/>.
    /// </summary>
    public static object? ToLogPayload(object? value, int maxBodyChars = DefaultMaxBodyChars)
    {
        if (value is null)
        {
            return null;
        }

        var json = RedactSecrets(JsonSerializer.Serialize(value, JsonFileStore.Options));
        if (json.Length <= maxBodyChars)
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }

        return Truncate(json, maxBodyChars);
    }
}
