using System.Net;
using System.Text;
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

    /// <summary>HTML comment marker so Chat JS can promote the bubble into a Trace preview card.</summary>
    public const string TracePreviewMarkerPrefix = "<!-- athlon-team-trace:";

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
            all.AddRange(CollectMemberFiles(task, taskStore, task.LeaderSessionId, isLeader: true));
        }

        foreach (var sub in task.SubTasks)
        {
            if (string.IsNullOrWhiteSpace(sub.MemberSessionId))
            {
                continue;
            }

            all.AddRange(CollectMemberFiles(task, taskStore, sub.MemberSessionId, isLeader: false));
        }

        return SessionModifiedFilesTracker.MergeByPath(all);
    }

    public static IReadOnlyList<ModifiedFileViewModel> CollectMemberFiles(
        TeamTask task,
        ITeamTaskStore taskStore,
        string memberSessionId,
        bool isLeader = false)
    {
        var messages = TeamTraceTranscriptBuilder.ReadMemberMessages(
            task, taskStore, memberSessionId, isLeader);
        return messages.Count == 0
            ? Array.Empty<ModifiedFileViewModel>()
            : SessionModifiedFilesTracker.CollectTurnFiles(messages);
    }

    public static string FormatStepMarkdown(TeamRunProgress step)
    {
        if (step.Kind == TeamRunProgressKind.TaskStarted)
        {
            return FormatTracePreviewMarkdown(new TeamTracePreviewState
            {
                TaskId = step.TaskId,
                Title = step.Title,
                StatusLabel = string.IsNullOrWhiteSpace(step.StatusLabel) ? "Running" : step.StatusLabel,
                GoalPreview = step.ResponseText
            });
        }

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

        const int maxBodyChars = 400;
        if (body.Length > maxBodyChars)
        {
            body = body[..maxBodyChars].TrimEnd() + "…\n\n_(full output in Teams Trace)_";
        }

        return $"### Teams · {title} · {status}{iter}\n\n{body}";
    }

    /// <summary>Live Trace preview card in Chat — click expands the full Trace window.</summary>
    public static string FormatTracePreviewMarkdown(TeamTracePreviewState state)
    {
        var taskId = (state.TaskId ?? "").Trim();
        var title = string.IsNullOrWhiteSpace(state.Title) ? "Teams" : state.Title.Trim();
        var status = string.IsNullOrWhiteSpace(state.StatusLabel) ? "Running" : state.StatusLabel.Trim();
        var sb = new StringBuilder();
        sb.Append(TracePreviewMarkerPrefix).Append(WebUtility.HtmlEncode(taskId)).AppendLine(" -->");
        sb.Append("### Teams · ").Append(title).AppendLine();
        sb.AppendLine();
        sb.Append("**").Append(status).Append("**");
        if (state.RecentLines.Count > 0)
        {
            sb.Append(" · ").Append(state.RecentLines.Count).Append(state.RecentLines.Count == 1 ? " update" : " updates");
        }

        sb.AppendLine();
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(state.GoalPreview))
        {
            var preview = state.GoalPreview.Trim();
            if (preview.Length > 200)
            {
                preview = preview[..200].TrimEnd() + "…";
            }

            sb.AppendLine(preview);
            sb.AppendLine();
        }

        if (state.RecentLines.Count > 0)
        {
            foreach (var line in state.RecentLines.TakeLast(3))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                sb.Append("- ").AppendLine(line.Trim());
            }

            sb.AppendLine();
        }

        sb.Append("[放大 Trace](athlon://team-trace?taskId=").Append(taskId).Append(')');
        return sb.ToString();
    }

    public static string FormatRecentProgressLine(TeamRunProgress step)
    {
        var role = step.Kind switch
        {
            TeamRunProgressKind.LeaderTurnCompleted => "Leader",
            TeamRunProgressKind.WorkerFailed => "Worker",
            TeamRunProgressKind.WorkerCancelled => "Worker",
            TeamRunProgressKind.WorkerCompleted => "Worker",
            _ => "Teams"
        };
        var title = string.IsNullOrWhiteSpace(step.Title) ? role : step.Title.Trim();
        var status = string.IsNullOrWhiteSpace(step.StatusLabel) ? step.Kind.ToString() : step.StatusLabel.Trim();
        if (step.Kind is TeamRunProgressKind.WorkerCompleted
            or TeamRunProgressKind.WorkerFailed
            or TeamRunProgressKind.WorkerCancelled)
        {
            return $"{title} · {status} · iter {Math.Max(1, step.Iteration)}";
        }

        return $"{title} · {status}";
    }

    /// <summary>Early Chat bubble so users can reopen Trace if the window was closed.</summary>
    public static string FormatTraceStartedMarkdown(
        string taskId,
        string? teamOrTitle = null,
        string? goalPreview = null) =>
        FormatTracePreviewMarkdown(new TeamTracePreviewState
        {
            TaskId = taskId,
            Title = teamOrTitle ?? "Teams",
            StatusLabel = "Running",
            GoalPreview = goalPreview
        });

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

/// <summary>Mutable state for the in-chat Trace preview bubble while a Team run is live.</summary>
internal sealed class TeamTracePreviewState
{
    public string TaskId { get; set; } = "";

    public string Title { get; set; } = "Teams";

    public string StatusLabel { get; set; } = "Running";

    public string? GoalPreview { get; set; }

    public List<string> RecentLines { get; } = new();

    public void RememberLine(string line, int maxLines = 3)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        RecentLines.Add(line.Trim());
        while (RecentLines.Count > maxLines)
        {
            RecentLines.RemoveAt(0);
        }
    }
}
