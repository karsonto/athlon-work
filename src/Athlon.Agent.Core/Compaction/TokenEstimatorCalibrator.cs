using System.Collections.Concurrent;

namespace Athlon.Agent.Core.Compaction;

public interface ITokenEstimatorCalibrator
{
    double GetMultiplier(string sessionId);

    void Observe(string sessionId, int estimatedPromptTokens, int? actualPromptTokens);
}

public sealed class TokenEstimatorCalibrator(AppSettings settings) : ITokenEstimatorCalibrator
{
    private readonly ConcurrentDictionary<string, double> _multipliers = new(StringComparer.Ordinal);

    public double GetMultiplier(string sessionId)
    {
        if (!settings.ContextCompaction.DynamicCompaction.EnableUsageCalibration
            || string.IsNullOrWhiteSpace(sessionId))
        {
            return 1.0;
        }

        return _multipliers.GetValueOrDefault(sessionId, 1.0);
    }

    public void Observe(string sessionId, int estimatedPromptTokens, int? actualPromptTokens)
    {
        if (!settings.ContextCompaction.DynamicCompaction.EnableUsageCalibration
            || string.IsNullOrWhiteSpace(sessionId)
            || actualPromptTokens is not > 0
            || estimatedPromptTokens <= 0)
        {
            return;
        }

        var alpha = Math.Clamp(settings.ContextCompaction.DynamicCompaction.UsageCalibrationAlpha, 0.01, 1.0);
        var observed = (double)actualPromptTokens.Value / estimatedPromptTokens;
        observed = Math.Clamp(observed, 0.5, 2.5);

        _multipliers.AddOrUpdate(
            sessionId,
            observed,
            (_, previous) => previous + alpha * (observed - previous));
    }
}
