namespace Athlon.Agent.Core;

/// <summary>
/// Whether prompt-cache metrics from the provider are usable for display.
/// </summary>
public enum PromptCacheAvailability
{
    /// <summary>No cache fields in the usage payload.</summary>
    Unknown,

    /// <summary>Explicit hit and miss token counts (e.g. DeepSeek native fields).</summary>
    HitMiss,

    /// <summary>Read-only cache token count without a reliable miss denominator.</summary>
    ReadOnly
}
