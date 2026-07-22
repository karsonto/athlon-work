namespace Athlon.Agent.Core.Harness;

public enum SessionAgentMode
{
    Agent = 0,
    Coding = 1,
    Ask = 2,
    Plan = 3,
}

public sealed class SessionHarnessFile
{
    public bool Enabled { get; set; }

    public string? Mode { get; set; }
}

public sealed record SessionHarnessSnapshot(SessionAgentMode Mode)
{
    public static SessionHarnessSnapshot Empty { get; } = new(SessionAgentMode.Agent);

    public static SessionHarnessSnapshot FromPersisted(SessionHarnessFile? file)
    {
        if (file is null)
        {
            return Empty;
        }

        if (!string.IsNullOrWhiteSpace(file.Mode)
            && TryParseMode(file.Mode, out var parsed))
        {
            return new SessionHarnessSnapshot(parsed);
        }

        return file.Enabled
            ? new SessionHarnessSnapshot(SessionAgentMode.Coding)
            : new SessionHarnessSnapshot(SessionAgentMode.Agent);
    }

    public static bool TryParseMode(string? value, out SessionAgentMode mode)
    {
        if (string.Equals(value, "agent", StringComparison.OrdinalIgnoreCase))
        {
            mode = SessionAgentMode.Agent;
            return true;
        }

        if (string.Equals(value, "coding", StringComparison.OrdinalIgnoreCase))
        {
            mode = SessionAgentMode.Coding;
            return true;
        }

        if (string.Equals(value, "ask", StringComparison.OrdinalIgnoreCase))
        {
            mode = SessionAgentMode.Ask;
            return true;
        }

        if (string.Equals(value, "plan", StringComparison.OrdinalIgnoreCase))
        {
            mode = SessionAgentMode.Plan;
            return true;
        }

        mode = SessionAgentMode.Agent;
        return false;
    }

    public string ToPersistedMode() => Mode switch
    {
        SessionAgentMode.Coding => "coding",
        SessionAgentMode.Ask => "ask",
        SessionAgentMode.Plan => "plan",
        _ => "agent",
    };
}

public interface ISessionHarnessState
{
    Task LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SaveAsync(string sessionId, SessionHarnessSnapshot state, CancellationToken cancellationToken = default);

    SessionHarnessSnapshot GetSnapshot(string? sessionId);

    SessionAgentMode GetMode(string? sessionId);

    bool IsCodingMode(string? sessionId);

    bool IsAskMode(string? sessionId);

    bool IsPlanMode(string? sessionId);

    bool IsEnabled(string? sessionId);

    bool IsCodingModeForActiveRun(IAgentRunContextAccessor runContextAccessor);

    bool IsAskModeForActiveRun(IAgentRunContextAccessor runContextAccessor);

    bool IsPlanModeForActiveRun(IAgentRunContextAccessor runContextAccessor);

    bool IsEnabledForActiveRun(IAgentRunContextAccessor runContextAccessor);
}
