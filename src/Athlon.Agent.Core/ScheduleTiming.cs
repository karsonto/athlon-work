namespace Athlon.Agent.Core;

public static class ScheduleTiming
{
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
            "at" => DateTime.TryParse(task.AtTime, out var at) && DateTime.UtcNow >= at.ToUniversalTime(),
            "daily" => false,
            _ => false
        };

    public static string ComputeNextRun(ScheduledTask task, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;

        return task.Kind switch
        {
            "daily" => ComputeNextDaily(task, now),
            "interval" when task.EveryMinutes > 0 => now.AddMinutes(task.EveryMinutes).ToString("O"),
            "at" when DateTime.TryParse(task.AtTime, out var at) && at.ToUniversalTime() > now => at.ToUniversalTime().ToString("O"),
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
