namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Tunables for budget-aware adjustment of the static compaction strategy.
/// Dynamic mode raises action thresholds toward <see cref="TargetUtilization"/>, then after a full
/// truncate → re-evict → compact pass lands near <see cref="PostCompactionUtilization"/>.
/// </summary>
public sealed class DynamicCompactionSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Raised trigger ceiling on the usable prompt window (default 80%).
    /// Compact / truncate begin when utilization reaches this band (static thresholds remain a floor).
    /// </summary>
    public double TargetUtilization { get; set; } = 0.80;

    /// <summary>
    /// After a full 3-level pass (truncateArgs → prefix re-evict → LLM compact),
    /// keep enough history to land near this utilization (default 30%).
    /// </summary>
    public double PostCompactionUtilization { get; set; } = 0.30;

    public double SafetyMarginRatio { get; set; } = 0.08;

    public int DefaultReservedOutputTokens { get; set; } = 8192;

    /// <summary>
    /// Begin truncateArgs / prefix re-evict when utilization reaches
    /// <c>TargetUtilization × TruncateLeadRatio</c> (default 0.90 → 72% at target 80%).
    /// </summary>
    public double TruncateLeadRatio { get; set; } = 0.90;

    /// <summary>On overflow retry, land near this utilization (default 20%).</summary>
    public double OverflowPostCompactionUtilization { get; set; } = 0.20;

    public bool EnableSemanticCutoff { get; set; } = true;

    public bool EnableUsageCalibration { get; set; } = true;

    public double UsageCalibrationAlpha { get; set; } = 0.15;
}
