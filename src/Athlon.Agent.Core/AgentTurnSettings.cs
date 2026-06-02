namespace Athlon.Agent.Core;

public sealed class AgentTurnSettings
{
    /// <summary>
    /// Single user-message agent loop timeout in minutes. Default 30.
    /// Set to <c>0</c> to disable the turn timeout. Positive values are clamped to 1–180.
    /// </summary>
    public int TimeoutMinutes { get; set; } = AgentTurnSettingsExtensions.DefaultTimeoutMinutes;
}

public static class AgentTurnSettingsExtensions
{
    public const int MinTimeoutMinutes = 1;
    public const int MaxTimeoutMinutes = 180;
    public const int DefaultTimeoutMinutes = 30;

    public static bool HasTurnTimeout(this AgentTurnSettings? settings) =>
        ResolveTurnTimeout(settings).HasValue;

    public static TimeSpan? ResolveTurnTimeout(this AgentTurnSettings? settings)
    {
        var minutes = settings?.TimeoutMinutes ?? DefaultTimeoutMinutes;
        if (minutes <= 0)
        {
            return null;
        }

        return TimeSpan.FromMinutes(Math.Clamp(minutes, MinTimeoutMinutes, MaxTimeoutMinutes));
    }

    public static int ResolveTurnTimeoutMinutes(this AgentTurnSettings? settings)
    {
        var timeout = settings.ResolveTurnTimeout();
        return timeout is null ? 0 : (int)timeout.Value.TotalMinutes;
    }
}
