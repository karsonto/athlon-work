namespace Athlon.Agent.Core.Compaction;

public sealed class DynamicCompactionSettings
{
    public bool Enabled { get; set; } = true;

    public double SafetyMarginRatio { get; set; } = 0.08;

    public int DefaultReservedOutputTokens { get; set; } = 8192;

    public double ElevatedUtilization { get; set; } = 0.55;

    public double HighUtilization { get; set; } = 0.72;

    public double CriticalUtilization { get; set; } = 0.88;

    public double KeepRatioElevated { get; set; } = 0.45;

    public double KeepRatioCritical { get; set; } = 0.35;

    public double KeepRatioOverflow { get; set; } = 0.25;

    public bool EnableSemanticCutoff { get; set; } = true;

    public bool EnableUsageCalibration { get; set; } = true;

    public double UsageCalibrationAlpha { get; set; } = 0.15;
}
