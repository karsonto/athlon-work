using Athlon.Agent.App.ViewModels;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Helpers for surfacing Team run steps and file edits into the root Chat timeline.
/// </summary>
internal static class TeamChatArtifacts
{
    private static ITeamTaskStore? _taskStore;

    /// <summary>Wired once from TeamsPageViewModel so Chat replay can rebuild FilesChanged cards.</summary>
    public static void ConfigureTaskStore(ITeamTaskStore taskStore) =>
        _taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));

    public static IReadOnlyList<ModifiedFileViewModel> CollectTaskFiles(
        TeamTask task,
        ITeamTaskStore taskStore)
    {
        var all = new List<ModifiedFileViewModel>();
        if (!string.IsNullOrWhiteSpace(task.LeaderSessionId))
        {
            all.AddRange(CollectMemberFiles(task, taskStore, task.LeaderSessionId));
        }

        foreach (var sub in task.SubTasks)
        {
            if (string.IsNullOrWhiteSpace(sub.MemberSessionId))
            {
                continue;
            }

            all.AddRange(CollectMemberFiles(task, taskStore, sub.MemberSessionId));
        }

        return SessionModifiedFilesTracker.MergeByPath(all);
    }

    public static IReadOnlyList<ModifiedFileViewModel> CollectMemberFiles(
        TeamTask task,
        ITeamTaskStore taskStore,
        string memberSessionId)
    {
        var messages = TeamTraceTranscriptBuilder.ReadMemberMessages(task, taskStore, memberSessionId);
        return messages.Count == 0
            ? Array.Empty<ModifiedFileViewModel>()
            : SessionModifiedFilesTracker.CollectTurnFiles(messages);
    }

    public static string FormatStepMarkdown(TeamRunProgress step)
    {
        var role = step.Kind switch
        {
            TeamRunProgressKind.LeaderTurnCompleted => "Leader",
            _ => "Worker"
        };
        var title = string.IsNullOrWhiteSpace(step.Title) ? role : step.Title.Trim();
        var status = string.IsNullOrWhiteSpace(step.StatusLabel) ? step.Kind.ToString() : step.StatusLabel;
        var iter = step.Kind == TeamRunProgressKind.LeaderTurnCompleted
            ? ""
            : $" · iter {Math.Max(1, step.Iteration)}";

        var body = !string.IsNullOrWhiteSpace(step.ResponseText)
            ? step.ResponseText.Trim()
            : (!string.IsNullOrWhiteSpace(step.Error) ? step.Error!.Trim() : "(empty)");

        return $"### Teams · {title} · {status}{iter}\n\n{body}";
    }

    /// <summary>
    /// Loads a Team task from the configured store and collects member file edits (safe for
    /// background replay threads).
    /// </summary>
    public static IReadOnlyList<ModifiedFileViewModel> TryCollectTaskFilesForReplay(
        string rootSessionId,
        string taskId)
    {
        if (_taskStore is null
            || string.IsNullOrWhiteSpace(rootSessionId)
            || string.IsNullOrWhiteSpace(taskId))
        {
            return Array.Empty<ModifiedFileViewModel>();
        }

        try
        {
            var task = _taskStore
                .GetAsync(rootSessionId, taskId, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            return task is null
                ? Array.Empty<ModifiedFileViewModel>()
                : CollectTaskFiles(task, _taskStore);
        }
        catch
        {
            return Array.Empty<ModifiedFileViewModel>();
        }
    }

    public static bool TryParseTeamTaskIdFromContent(string? content, out string taskId)
    {
        taskId = "";
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        // athlon://team-trace?taskId=tm_xxx  or athlon://team-trace/tm_xxx
        const string prefix = "athlon://team-trace";
        var idx = content.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var slice = content[(idx + prefix.Length)..];
        if (slice.StartsWith('?'))
        {
            var q = slice[1..];
            foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = part[..eq];
                if (!string.Equals(key, "taskId", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                taskId = part[(eq + 1)..].Trim();
                // Stop at markdown link closers / whitespace
                var cut = taskId.IndexOfAny([' ', ')', ']', '"', '\'', '\r', '\n']);
                if (cut >= 0)
                {
                    taskId = taskId[..cut];
                }

                return !string.IsNullOrWhiteSpace(taskId);
            }
        }

        slice = slice.TrimStart('/', '?', ' ');
        if (slice.Length == 0)
        {
            return false;
        }

        var end = slice.IndexOfAny([' ', ')', ']', '"', '\'', '\r', '\n', '&', '?']);
        taskId = end >= 0 ? slice[..end] : slice;
        return !string.IsNullOrWhiteSpace(taskId);
    }
}
