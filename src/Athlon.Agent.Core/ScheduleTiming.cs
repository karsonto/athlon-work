namespace Athlon.Agent.Core;

public static class ScheduleTiming
{
    /// <summary>
    /// Exact format written by the schedule editor and persisted in <c>settings.json</c>.
    /// Parsing must not depend on the ambient culture, otherwise a non-ISO culture can
    /// misread a stored one-time value and fire the task at the wrong moment.
    /// </summary>
    public const string AtTimeFormat = "yyyy-MM-dd HH:mm";

    public static bool IsDue(ScheduledTask task, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(task.NextRunAt))
        {
            return ShouldRunImmediately(task);
        }

        if (!DateTime.TryParse(task.NextRunAt, out var nextRun))
        {
            return ShouldRunImmediately(task);
        }

        return now >= nextRun.ToUniversalTime();
    }

    public static bool ShouldRunImmediately(ScheduledTask task) =>
        task.Kind switch
        {
            "manual" => false,
            "interval" => task.EveryMinutes > 0 && string.IsNullOrWhiteSpace(task.LastRunAt),
            "at" => TryParseAtTime(task.AtTime, out var at) && DateTime.UtcNow >= at.ToUniversalTime(),
            "daily" => false,
            _ => false
        };

    /// <summary>
    /// True when a one-time task already fired and was auto-disabled by the scheduler. Used by the
    /// UI to render "completed" instead of a blank next-run, and by the editor to allow rescheduling.
    /// </summary>
    public static bool IsConsumedOneShot(ScheduledTask task) =>
        string.Equals(task.Kind, "at", StringComparison.OrdinalIgnoreCase)
        && !task.Enabled
        && !string.IsNullOrWhiteSpace(task.LastRunAt);

    /// <summary>True when a one-time task is armed for a moment that is still in the future.</summary>
    public static bool IsOneShotScheduledInFuture(ScheduledTask task, DateTime? utcNow = null) =>
        string.Equals(task.Kind, "at", StringComparison.OrdinalIgnoreCase)
        && TryParseAtTime(task.AtTime, out var at)
        && at.ToUniversalTime() > (utcNow ?? DateTime.UtcNow);

    /// <summary>
    /// Parses a stored one-time value. Prefers the exact editor format (culture-independent) and
    /// falls back to the lenient parser so values written by older builds keep working.
    /// </summary>
    public static bool TryParseAtTime(string? value, out DateTime local)
    {
        local = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return DateTime.TryParseExact(
                   value,
                   AtTimeFormat,
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None,
                   out local)
            || DateTime.TryParse(value, out local);
    }

    public static string ComputeNextRun(ScheduledTask task, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;

        return task.Kind switch
        {
            "daily" => ComputeNextDaily(task, now),
            "interval" when task.EveryMinutes > 0 => now.AddMinutes(task.EveryMinutes).ToString("O"),
            "at" when TryParseAtTime(task.AtTime, out var at) && at.ToUniversalTime() > now => at.ToUniversalTime().ToString("O"),
            "manual" => "",
            _ => ""
        };
    }

    public static string ComputeNextDaily(ScheduledTask task, DateTime? utcNow = null)
    {
        if (!TimeSpan.TryParse(task.TimeOfDay, out var tod))
        {
            return "";
        }

        var nowLocal = (utcNow ?? DateTime.UtcNow).ToLocalTime();
        var next = nowLocal.Date + tod;
        if (next <= nowLocal)
        {
            next = next.AddDays(1);
        }

        return next.ToUniversalTime().ToString("O");
    }

    public static void EnsureNextRunAt(ScheduledTask task, DateTime? utcNow = null)
    {
        if (task.Kind == "manual")
        {
            task.NextRunAt = "";
            return;
        }

        task.NextRunAt = ComputeNextRun(task, utcNow);
    }

    public static string ResolveMode(ScheduledTask task, ScheduleSettings schedule)
    {
        if (!string.IsNullOrWhiteSpace(task.Mode) && !string.Equals(task.Mode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return task.Mode;
        }

        return string.IsNullOrWhiteSpace(schedule.Mode) ? "agent" : schedule.Mode;
    }

    public static string? ResolveModelName(ScheduledTask task, ScheduleSettings schedule, string globalModelName)
    {
        if (!string.IsNullOrWhiteSpace(task.Model) && !string.Equals(task.Model, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return task.Model;
        }

        if (!string.IsNullOrWhiteSpace(schedule.Model) && !string.Equals(schedule.Model, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return schedule.Model;
        }

        return globalModelName;
    }

    public static (bool AllowToolCalls, int? MaxModelToolRounds) ResolveModeOptions(string mode) =>
        mode.ToLowerInvariant() switch
        {
            "ask" => (false, null),
            _ => (true, null)
        };

    public static string BuildPrompt(ScheduledTask task, ScheduleSettings schedule)
    {
        var prompt = task.Prompt ?? "";
        var prefix = schedule.PromptPrefix?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return prompt;
        }

        return string.IsNullOrWhiteSpace(prompt) ? prefix : $"{prefix}\n{prompt}";
    }

    public static string ResolveWorkspaceRoot(ScheduledTask task, ScheduleSettings? schedule = null)
    {
        var taskRoot = task.WorkspaceRoot?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(taskRoot))
        {
            return taskRoot;
        }

        return schedule?.DefaultWorkspaceRoot?.Trim() ?? "";
    }

    /// <summary>
    /// Empty task list = inherit all globally enabled names; non-empty = intersection.
    /// Returns null when unrestricted (no schedule filter).
    /// </summary>
    public static IReadOnlyList<string>? ResolveAllowList(
        IReadOnlyList<string>? taskNames,
        IEnumerable<string> globallyEnabledNames)
    {
        var global = globallyEnabledNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (taskNames is null || taskNames.Count == 0)
        {
            return null;
        }

        var wanted = new HashSet<string>(
            taskNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);

        return global.Where(wanted.Contains).ToArray();
    }
}
