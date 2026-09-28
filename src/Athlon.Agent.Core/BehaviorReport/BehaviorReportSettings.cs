namespace Athlon.Agent.Core.BehaviorReport;

public sealed class BehaviorReportSettings
{
    /// <summary>When false, Record / upload are no-ops.</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL of the report API (POST {BaseUrl}/agent/report).</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Cadence for flushing real business events.</summary>
    public int UploadIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Fixed cadence for the liveness heartbeat. Deliberately independent of
    /// <see cref="UploadIntervalMinutes"/>: the heartbeat carries the version/IP envelope, so the
    /// roster stays current even when the business-event interval is tuned for batching (or when an
    /// idle Agent produces no business events at all).
    /// </summary>
    public int HeartbeatIntervalMinutes { get; set; } = 5;
}
