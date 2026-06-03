namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Tunables for budget-aware adjustment of the static compaction strategy.
/// Dynamic mode does not define a parallel strategy — it scales static triggers and keep windows
/// from a single <see cref="TargetUtilization"/> ceiling (default 80% of the usable context window).
/// </summary>
public sealed class DynamicCompactionSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Target share of the usable prompt window (total window minus reserved output).</summary>
    public double TargetUtilization { get; set; } = 0.80;

    public double SafetyMarginRatio { get; set; } = 0.08;

    public int DefaultReservedOutputTokens { get; set; } = 8192;

    /// <summary>
    /// Begin truncateArgs / prefix re-evict when utilization reaches
    /// <c>TargetUtilization × TruncateLeadRatio</c> (default 0.90 → 72% at target 80%).
    /// </summary>
    public double TruncateLeadRatio { get; set; } = 0.90;

    /// <summary>
    /// On overflow retry, keep enough history to land near
    /// <c>TargetUtilization × OverflowKeepRatio</c> (default 0.50 → 40%).
    /// </summary>
    public double OverflowKeepRatio { get; set; } = 0.50;

    public bool EnableSemanticCutoff { get; set; } = true;

    public bool EnableUsageCalibration { get; set; } = true;

    public double UsageCalibrationAlpha { get; set; } = 0.15;
}
