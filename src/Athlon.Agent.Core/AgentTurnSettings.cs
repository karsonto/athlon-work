namespace Athlon.Agent.Core;

public sealed class AgentTurnSettings
{
    /// <summary>
    /// Single user-message agent loop timeout in minutes. Default 0 (disabled).
    /// Set to <c>0</c> to disable the turn timeout. Positive values are clamped to 1–180.
    /// </summary>
    public int TimeoutMinutes { get; set; } = AgentTurnSettingsExtensions.DefaultTimeoutMinutes;

    /// <summary>
    /// When true, an approved Session Plan keeps running: if a turn ends while the session task
    /// list still has open items, a continuation turn starts automatically.
    /// </summary>
    public bool PlanAutoContinueEnabled { get; set; } = true;

    /// <summary>
    /// Maximum consecutive auto-continuation turns per Build. Clamped to 1–50.
    /// Guards against a runaway loop when the model never converges.
    /// </summary>
    public int PlanAutoContinueMaxRounds { get; set; } = AgentTurnSettingsExtensions.DefaultPlanAutoContinueMaxRounds;

    /// <summary>
    /// When true, the task list and plan artifacts are cleared once every task is completed.
    /// </summary>
    public bool ClearPlanOnAllTasksCompleted { get; set; } = true;
}

public static class AgentTurnSettingsExtensions
{
    public const int MinTimeoutMinutes = 1;
    public const int MaxTimeoutMinutes = 180;
    public const int DefaultTimeoutMinutes = 0;

    public const int MinPlanAutoContinueMaxRounds = 1;
    public const int MaxPlanAutoContinueMaxRounds = 50;
    public const int DefaultPlanAutoContinueMaxRounds = 12;

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

    public static bool ResolvePlanAutoContinueEnabled(this AgentTurnSettings? settings) =>
        settings?.PlanAutoContinueEnabled ?? true;

    public static int ResolvePlanAutoContinueMaxRounds(this AgentTurnSettings? settings) =>
        Math.Clamp(
            settings?.PlanAutoContinueMaxRounds ?? DefaultPlanAutoContinueMaxRounds,
            MinPlanAutoContinueMaxRounds,
            MaxPlanAutoContinueMaxRounds);

    public static bool ResolveClearPlanOnAllTasksCompleted(this AgentTurnSettings? settings) =>
        settings?.ClearPlanOnAllTasksCompleted ?? true;
}
