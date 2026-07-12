namespace Athlon.Agent.Core;

public enum AgentAttemptKind
{
    Model,
    Tool
}

public sealed record AgentAttemptEvent(
    DateTimeOffset Timestamp,
    string AttemptId,
    string SessionId,
    string TurnId,
    AgentAttemptKind Kind,
    ModelCallPurpose Purpose,
    string? Tool,
    string? SchemaFingerprint,
    string? Model,
    int Prompt,
    int Completion,
    string Result,
    string? ErrorCode,
    long Latency,
    string? ParentAttemptId = null,
    string? InputFingerprint = null);

public sealed record AgentRoiMetrics(
    int TurnsWithTools,
    int ToolAttempts,
    double FirstToolSuccessRate,
    double RepeatCallRate,
    double AverageTokensToSuccess);

public static class AgentRoiMetricsCalculator
{
    public static AgentRoiMetrics Compute(IEnumerable<AgentAttemptEvent> events)
    {
        var attempts = events.OrderBy(item => item.Timestamp).ToArray();
        var toolAttempts = attempts.Where(item => item.Kind == AgentAttemptKind.Tool).ToArray();
        var turns = toolAttempts.GroupBy(item => (item.SessionId, item.TurnId)).ToArray();
        var firstSuccesses = turns.Count(turn => IsSuccess(turn.First()));

        var repeated = 0;
        foreach (var turn in turns)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attempt in turn)
            {
                var signature = $"{attempt.Tool}\n{attempt.InputFingerprint}";
                if (!seen.Add(signature))
                {
                    repeated++;
                }
            }
        }

        var tokenSamples = new List<int>();
        foreach (var turn in turns)
        {
            var success = turn.FirstOrDefault(IsSuccess);
            if (success is null)
            {
                continue;
            }

            tokenSamples.Add(attempts
                .Where(item => item.SessionId == success.SessionId
                    && item.TurnId == success.TurnId
                    && item.Timestamp <= success.Timestamp
                    && item.Kind == AgentAttemptKind.Model)
                .Sum(item => item.Prompt + item.Completion));
        }

        return new AgentRoiMetrics(
            turns.Length,
            toolAttempts.Length,
            turns.Length == 0 ? 0 : (double)firstSuccesses / turns.Length,
            toolAttempts.Length == 0 ? 0 : (double)repeated / toolAttempts.Length,
            tokenSamples.Count == 0 ? 0 : tokenSamples.Average());
    }

    private static bool IsSuccess(AgentAttemptEvent attempt) =>
        string.Equals(attempt.Result, "success", StringComparison.OrdinalIgnoreCase);
}
