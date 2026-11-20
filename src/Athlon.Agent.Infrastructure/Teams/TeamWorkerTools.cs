using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.Infrastructure.Teams;

/// <summary>Worker structured handoff — authoritative over free-text ## Result when invoked.</summary>
public sealed class TeamReportResultTool(
    AppSettings settings,
    ITeamTaskStore taskStore,
    ITeamTraceStore traceStore) : IAgentTool
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed", "failed", "blocked", "needs_leader"
    };

    public ToolDefinition Definition => new(
        TeamToolDefaults.ReportResultTool,
        "Submit this Worker's structured result (preferred over a free-text ## Result block). "
        + "Call once when finished. status: completed|failed|blocked|needs_leader.",
        ToolSchema.Object()
            .String("status", "completed | failed | blocked | needs_leader", required: true, minLength: 1)
            .String("findings", "Findings / risks / leftovers (bullet text ok)", required: true, minLength: 1)
            .String("accomplished", "What you finished (optional bullet text)")
            .String("verdict", "Optional verification verdict: pass | fail")
            .String("fix_target_subtask_id", "Optional implementer subtask id when verdict=fail")
            .String("artifact_paths", "Optional comma/newline-separated artifact paths")
            .Build(),
        Group: ToolGroup.Builtin);

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!settings.Teams.Enabled)
        {
            return ToolResult.Failure("Teams disabled", "Teams feature is disabled.");
        }

        if (AgentProfileTurnScope.Current?.Options.IsLeader == true)
        {
            return ToolResult.Failure(
                "Leader cannot report",
                "team_report_result is for Workers only. Leaders use team_submit_result.");
        }

        invocation.Arguments.TryGetString("status", out var statusRaw);
        invocation.Arguments.TryGetString("findings", out var findings);
        invocation.Arguments.TryGetString("accomplished", out var accomplished);
        invocation.Arguments.TryGetString("verdict", out var verdictRaw);
        invocation.Arguments.TryGetString("fix_target_subtask_id", out var fixTarget);
        invocation.Arguments.TryGetString("artifact_paths", out var artifactsRaw);

        var statusKey = (statusRaw ?? "").Trim().ToLowerInvariant();
        if (!AllowedStatuses.Contains(statusKey))
        {
            return ToolResult.Failure(
                "Invalid status",
                "status must be completed, failed, blocked, or needs_leader.");
        }

        if (string.IsNullOrWhiteSpace(findings))
        {
            return ToolResult.Failure("Invalid args", "findings is required.");
        }

        string? verdict = null;
        if (!string.IsNullOrWhiteSpace(verdictRaw))
        {
            verdict = verdictRaw.Trim().ToLowerInvariant();
            if (verdict is not ("pass" or "fail"))
            {
                return ToolResult.Failure("Invalid verdict", "verdict must be pass or fail when set.");
            }
        }

        var status = statusKey switch
        {
            "completed" => TeamMemberResultStatus.Completed,
            "failed" => TeamMemberResultStatus.Failed,
            "blocked" => TeamMemberResultStatus.Blocked,
            _ => TeamMemberResultStatus.NeedsLeader
        };

        var artifacts = TeamToolHelpers.ParseArtifactRefs(artifactsRaw);
        var responseText = BuildResultMarkdown(statusKey, verdict, fixTarget, accomplished, findings);
        var buffer = TeamWorkerReportBuffer.Current;
        if (buffer is null)
        {
            return ToolResult.Failure(
                "No worker turn",
                "team_report_result requires an active Worker turn buffer.");
        }

        buffer.Report = new TeamWorkerReportedResult(
            status,
            verdict,
            string.IsNullOrWhiteSpace(fixTarget) ? null : fixTarget.Trim(),
            responseText,
            artifacts);

        var options = AgentProfileTurnScope.Current?.Options;
        if (options is not null
            && !string.IsNullOrWhiteSpace(options.RootSessionId)
            && !string.IsNullOrWhiteSpace(options.TeamTaskId)
            && !string.IsNullOrWhiteSpace(options.SubTaskId)
            && artifacts.Count > 0)
        {
            using (await TeamTaskLock.AcquireAsync(
                           options.RootSessionId,
                           options.TeamTaskId,
                           cancellationToken)
                       .ConfigureAwait(false))
            {
                var task = await taskStore
                    .GetAsync(options.RootSessionId, options.TeamTaskId, cancellationToken)
                    .ConfigureAwait(false);
                var sub = task?.SubTasks.FirstOrDefault(s =>
                    string.Equals(s.Id, options.SubTaskId, StringComparison.OrdinalIgnoreCase));
                if (sub is not null)
                {
                    foreach (var path in artifacts)
                    {
                        if (!sub.ArtifactRefs.Contains(path, StringComparer.OrdinalIgnoreCase))
                        {
                            sub.ArtifactRefs.Add(path);
                        }
                    }

                    await taskStore.SaveAsync(task!, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (options is not null
            && !string.IsNullOrWhiteSpace(options.RootSessionId)
            && !string.IsNullOrWhiteSpace(options.TeamTaskId))
        {
            await traceStore.AppendAsync(
                options.RootSessionId,
                options.TeamTaskId,
                new TeamTraceEvent
                {
                    Type = "worker.reported",
                    NodeId = options.SubTaskId ?? "worker",
                    SubTaskId = options.SubTaskId,
                    Message = $"status={statusKey}; verdict={verdict ?? "-"}"
                },
                cancellationToken).ConfigureAwait(false);
        }

        return ToolResult.Success(
            "reported",
            $"Recorded status={statusKey}. You may stop now; do not emit a duplicate ## Result unless needed.");
    }

    internal static string BuildResultMarkdown(
        string status,
        string? verdict,
        string? fixTarget,
        string? accomplished,
        string findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Result");
        sb.AppendLine($"status: {status}");
        if (!string.IsNullOrWhiteSpace(verdict))
        {
            sb.AppendLine($"verdict: {verdict}");
        }

        if (!string.IsNullOrWhiteSpace(fixTarget))
        {
            sb.AppendLine($"fix_target_subtask_id: {fixTarget.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(accomplished))
        {
            sb.AppendLine("accomplished:");
            foreach (var line in accomplished.ReplaceLineEndings("\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                sb.AppendLine(trimmed.StartsWith('-') ? trimmed : "- " + trimmed);
            }
        }

        sb.AppendLine("findings:");
        foreach (var line in findings.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            sb.AppendLine(trimmed.StartsWith('-') ? trimmed : "- " + trimmed);
        }

        return sb.ToString();
    }
}
