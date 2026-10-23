namespace Athlon.Agent.Core.Compaction;

/// <summary>
/// Budget-aware checkpoint triggers. When enabled, compact when total utilization reaches
/// <see cref="TargetUtilization"/> (or Overflow/Force).
/// </summary>
public sealed class DynamicCompactionSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Checkpoint when utilization reaches this band (default 80%).</summary>
    public double TargetUtilization { get; set; } = 0.80;

    public double SafetyMarginRatio { get; set; } = 0.08;

    public int DefaultReservedOutputTokens { get; set; } = 8192;

    public bool EnableUsageCalibration { get; set; } = true;

    public double UsageCalibrationAlpha { get; set; } = 0.15;
}
