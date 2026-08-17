using Athlon.Agent.Core;
using Athlon.Agent.Core.Debug;

namespace Athlon.Agent.Infrastructure.Debug;

public sealed class DebugReadLogsTool(
    IDebugPhaseAccessor phaseAccessor,
    IActiveAgentSessionContext activeSessionContext) : IAgentTool, ILocalWorkspaceTool, IDebugTool
{
    public ToolDefinition Definition { get; } = new(
        "debug_read_logs",
        "Read runtime debug JSONL logs captured during bug reproduction. "
            + "Each line is JSON: {\"ts\":\"...\",\"runId\":\"...\",\"hypothesisId\":\"H1\",\"location\":\"File.cs:42\",\"message\":\"...\",\"data\":{}}. "
            + "Use during Analyze phase after the user reproduces the bug.",
        ToolSchema.Object()
            .String("path", "Optional absolute path to JSONL log file; defaults to the active debug run log path")
            .String("hypothesis_id", "Optional hypothesis id filter, e.g. H1")
            .String("since", "Optional ISO-8601 lower bound timestamp")
            .String("until", "Optional ISO-8601 upper bound timestamp")
            .Integer("limit", "Maximum entries to return (default 200)", defaultValue: 200, minimum: 1)
            .Integer("tail", "Return only the last N entries after filtering", minimum: 1)
            .Build());

    public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var run = phaseAccessor.GetActiveRun(activeSessionContext.SessionId);
        var path = invocation.Arguments.GetString("path");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = run?.LogPath;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(ToolResult.Failure(
                "No debug log path",
                "Start a Debug mode investigation first or pass an explicit `path`."));
        }

        DateTimeOffset? since = null;
        if (invocation.Arguments.TryGetString("since", out var sinceText)
            && DateTimeOffset.TryParse(sinceText, out var sinceValue))
        {
            since = sinceValue;
        }

        DateTimeOffset? until = null;
        if (invocation.Arguments.TryGetString("until", out var untilText)
            && DateTimeOffset.TryParse(untilText, out var untilValue))
        {
            until = untilValue;
        }

        var hypothesisId = invocation.Arguments.GetString("hypothesis_id");
        var limit = invocation.Arguments.GetInt32("limit", 200);
        int? tail = invocation.Arguments.TryGetInt32("tail", out var tailValue) ? tailValue : null;

        var result = DebugLogReader.Read(path, hypothesisId, since, until, limit, tail);
        return Task.FromResult(ToolResult.Success(result.Summary, result.Body));
    }
}
