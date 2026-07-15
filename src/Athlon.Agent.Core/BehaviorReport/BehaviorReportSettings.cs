namespace Athlon.Agent.Core.BehaviorReport;

public sealed class BehaviorReportSettings
{
    /// <summary>When false, Record / upload are no-ops.</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL of the report API (POST {BaseUrl}/agent/report).</summary>
    public string BaseUrl { get; set; } = "";

    public int UploadIntervalMinutes { get; set; } = 10;
}
