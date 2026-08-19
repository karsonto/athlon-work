using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.RuntimeDiagnostics;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.Infrastructure.RuntimeDiagnostics;

/// <summary>
/// Reads <c>sessions/&lt;sessionId&gt;/diagnostics/runtime-events.jsonl</c> (or logs fallback) and
/// produces a machine-first diagnostic report consumable by analyze-phase prompting.
/// </summary>
public sealed class DiagnoseLogsTool(
    IAppPathProvider paths,
    IAgentRunContextAccessor runContextAccessor,
    IActiveAgentSessionContext activeSessionContext) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "diagnose_logs",
        "Diagnose runtime failures from structured runtime diagnostics events captured to runtime-events.jsonl.",
        ToolSchema.Object()
            .String("session_id", "Optional session id; defaults to active session id")
            .String("run_id", "Optional run id to filter")
            .String("component", "Optional runtime component, e.g. UiWebview / Model / Tool")
            .String("error_code", "Optional runtime error code to filter, e.g. ui.webview_init_failed")
            .String("since", "Optional ISO-8601 lower bound (inclusive)")
            .String("until", "Optional ISO-8601 upper bound (inclusive)")
            .Integer("limit", "Maximum matching events to scan (default 5000)", defaultValue: 5000, minimum: 1)
            .Integer("tail", "Return only the last N matching events after filtering (default 50)", defaultValue: 50, minimum: 1)
            .Build());

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var sessionId = invocation.Arguments.GetString("session_id") ?? activeSessionContext.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ToolResult.Failure(
                "diagnose_logs requires a session id",
                "Provide `session_id` or run within an active session.");
        }

        var path = ResolveRuntimeEventsPath(sessionId);
        if (!File.Exists(path))
        {
            return ToolResult.Failure(
                "runtime-events.jsonl not found",
                $"Expected runtime diagnostics log at: {path}");
        }

        var runId = invocation.Arguments.GetString("run_id");

        RuntimeDiagnosticComponent? component = null;
        var componentText = invocation.Arguments.GetString("component");
        if (!string.IsNullOrWhiteSpace(componentText)
            && Enum.TryParse(componentText, ignoreCase: true, out RuntimeDiagnosticComponent parsedComponent))
        {
            component = parsedComponent;
        }

        var errorCode = invocation.Arguments.GetString("error_code");

        DateTimeOffset? since = null;
        var sinceText = invocation.Arguments.GetString("since");
        if (!string.IsNullOrWhiteSpace(sinceText)
            && DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sinceValue))
        {
            since = sinceValue;
        }

        DateTimeOffset? until = null;
        var untilText = invocation.Arguments.GetString("until");
        if (!string.IsNullOrWhiteSpace(untilText)
            && DateTimeOffset.TryParse(untilText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var untilValue))
        {
            until = untilValue;
        }

        var limit = invocation.Arguments.GetInt32("limit", 5000);
        var tail = invocation.Arguments.GetInt32("tail", 50);

        // Read and filter in-memory: runtime-events.jsonl is typically small enough for analyze-phase.
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        var matching = new List<RuntimeDiagnosticEvent>(capacity: Math.Min(lines.Length, limit));

        foreach (var line in lines)
        {
            if (matching.Count >= limit)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            RuntimeDiagnosticEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<RuntimeDiagnosticEvent>(
                    line,
                    JsonFileStore.JsonLineOptions);
            }
            catch
            {
                continue;
            }

            if (evt is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(evt.sessionId)
                && !string.Equals(evt.sessionId, sessionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(runId)
                && !string.Equals(evt.runId, runId, StringComparison.Ordinal))
            {
                continue;
            }

            if (component is { } c
                && evt.component != c)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(errorCode)
                && !string.Equals(evt.errorCode, errorCode, StringComparison.Ordinal))
            {
                continue;
            }

            if (evt.ts != default)
            {
                if (since is { } s && evt.ts < s)
                {
                    continue;
                }

                if (until is { } u && evt.ts > u)
                {
                    continue;
                }
            }

            matching.Add(evt);
        }

        // Produce timeline and a basic root-cause suggestion.
        matching.Sort((a, b) => a.ts.CompareTo(b.ts));
        if (matching.Count > tail)
        {
            matching = matching.Skip(matching.Count - tail).ToList();
        }

        var errorCodes = matching
            .Select(e => e.errorCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .ToArray();

        var errorCodeCounts = errorCodes
            .GroupBy(code => code, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => (object)new { errorCode = g.Key, count = g.Count() })
            .ToArray();

        var primary = SelectPrimaryEvidence(matching);

        var rootCause = primary?.errorCode ?? (matching.Count == 0 ? null : "unknown");
        var evidenceQuality = BuildEvidenceQuality(matching);
        var confidence = ComputeConfidence(primary, evidenceQuality.Score);

        var timeline = matching.Select(e => (object)new
        {
            ts = e.ts == default ? (DateTimeOffset?)null : e.ts,
            component = e.component.ToString(),
            phase = e.phase.ToString(),
            severity = e.severity.ToString(),
            eventType = e.eventType,
            errorCode = e.errorCode,
            message = e.message
        }).ToArray();

        var nextChecks = BuildNextChecks(rootCause);
        var missingSignals = BuildMissingSignals(matching, evidenceQuality);

        var report = new DiagnoseLogsReport(
            summary: BuildSummary(rootCause, errorCodeCounts),
            rootCause: rootCause,
            confidence: confidence,
            errorCodes: errorCodeCounts,
            primaryEvidence: primary is null
                ? null
                : new
                {
                    eventType = primary.eventType,
                    errorCode = primary.errorCode,
                    component = primary.component.ToString(),
                    phase = primary.phase.ToString(),
                    ts = primary.ts == default ? (DateTimeOffset?)null : primary.ts,
                    message = primary.message
                },
            timeline: timeline,
            missingSignals: missingSignals,
            evidenceQuality: new
            {
                score = evidenceQuality.Score,
                runIdCoverage = evidenceQuality.RunIdCoverage,
                sessionIdCoverage = evidenceQuality.SessionIdCoverage,
                attemptIdCoverage = evidenceQuality.AttemptIdCoverage,
                toolCallIdCoverage = evidenceQuality.ToolCallIdCoverage
            },
            nextChecks: nextChecks);

        var content = JsonSerializer.Serialize(report, JsonFileStore.Options);
        return ToolResult.Success("diagnose_logs generated report", content);
    }

    private string ResolveRuntimeEventsPath(string sessionId)
    {
        var sessionsPath = paths.SessionsPath;
        var resolved = runContextAccessor.ResolveSessionDirectory(sessionsPath, sessionId);

        // Mirror sink resolution rules (best-effort).
        if (runContextAccessor.Current?.Kind == AgentRunKind.SubAgent)
        {
            return Path.Combine(resolved, "diagnostics", "runtime-events.jsonl");
        }

        if (SessionDirectoryLayout.IsTopLevelSessionDirectory(sessionsPath, resolved)
            && SessionDirectoryLayout.TryFindNestedSubAgentDirectory(sessionsPath, sessionId) is { } nested)
        {
            resolved = nested;
        }

        return Path.Combine(resolved, "diagnostics", "runtime-events.jsonl");
    }

    private static string BuildSummary(string? rootCause, IEnumerable<object> errorCodeCounts)
    {
        var rc = string.IsNullOrWhiteSpace(rootCause) ? "unknown" : rootCause;
        return $"diagnose_logs: suspected rootCause={rc}; errorCodeCounts={errorCodeCounts.Count()}";
    }

    private static string[] BuildNextChecks(string? rootCause)
    {
        if (string.IsNullOrWhiteSpace(rootCause))
        {
            return new[] { "No errorCode detected; broaden filters or reproduce the failure." };
        }

        return rootCause switch
        {
            RuntimeDiagnosticErrorCodes.UiWebviewInitFailed => new[]
            {
                "Verify WebView2 runtime/bundled runtime availability and check WebView2 bundled folder exists.",
                "Reproduce and capture app logs around WebChatView initialization failures.",
            },
            RuntimeDiagnosticErrorCodes.UiWebviewScriptFailed => new[]
            {
                "Verify chat page HTML/virtual host mapping is reachable.",
                "Inspect WebChatView script length/ExecuteScriptWhenReady failure reasons in runtime events timeline."
            },
            RuntimeDiagnosticErrorCodes.UiSessionSwitchSurfaceMismatch => new[]
            {
                "Check SessionRuntimeStore surfaceFingerprint consistency and ensure WebView replay cache is not reused across incompatible sessions.",
            },
            _ => new[]
            {
                "Open the timeline and locate the first occurrence of the suspected errorCode; then inspect adjacent request/invoke/persist events.",
                "If evidence is missing, add runtime instrumentation to the missing failure points described by the timeline."
            }
        };
    }

    private static RuntimeDiagnosticEvent? SelectPrimaryEvidence(IReadOnlyList<RuntimeDiagnosticEvent> matching)
    {
        if (matching.Count == 0)
        {
            return null;
        }

        RuntimeDiagnosticEvent? best = null;
        var bestScore = int.MinValue;
        for (var i = 0; i < matching.Count; i++)
        {
            var e = matching[i];
            if (string.IsNullOrWhiteSpace(e.errorCode))
            {
                continue;
            }

            var severityScore = e.severity switch
            {
                RuntimeDiagnosticSeverity.Critical => 500,
                RuntimeDiagnosticSeverity.Error => 400,
                RuntimeDiagnosticSeverity.Warning => 300,
                RuntimeDiagnosticSeverity.Info => 200,
                _ => 100
            };

            var phaseScore = e.phase switch
            {
                RuntimeDiagnosticPhase.Request => 80,
                RuntimeDiagnosticPhase.Initialize => 70,
                RuntimeDiagnosticPhase.Streaming => 60,
                RuntimeDiagnosticPhase.Parse => 55,
                RuntimeDiagnosticPhase.Invoke => 50,
                RuntimeDiagnosticPhase.Persist => 40,
                RuntimeDiagnosticPhase.Compact => 35,
                RuntimeDiagnosticPhase.Upload => 30,
                RuntimeDiagnosticPhase.Switch => 25,
                RuntimeDiagnosticPhase.Replay => 20,
                RuntimeDiagnosticPhase.Prepare => 10,
                _ => 0
            };

            // Favor earlier first-failure evidence when severity and phase are equal.
            var score = severityScore + phaseScore - i;
            if (score > bestScore)
            {
                best = e;
                bestScore = score;
            }
        }

        return best;
    }

    private static double ComputeConfidence(RuntimeDiagnosticEvent? primary, double evidenceScore)
    {
        if (primary is null)
        {
            return 0.0;
        }

        var severityBonus = primary.severity switch
        {
            RuntimeDiagnosticSeverity.Critical => 0.25,
            RuntimeDiagnosticSeverity.Error => 0.20,
            RuntimeDiagnosticSeverity.Warning => 0.10,
            _ => 0.05
        };

        return Math.Min(1.0, 0.45 + severityBonus + evidenceScore * 0.35);
    }

    private static string[] BuildMissingSignals(
        IReadOnlyList<RuntimeDiagnosticEvent> events,
        EvidenceQuality quality)
    {
        var missing = new List<string>();
        if (events.Count == 0)
        {
            missing.Add("No matching runtime events; reproduce failure with same session and rerun diagnose_logs.");
            return missing.ToArray();
        }

        if (quality.RunIdCoverage < 0.8)
        {
            missing.Add("Low runId coverage; correlate errors by session timeline only.");
        }

        if (quality.AttemptIdCoverage < 0.5)
        {
            missing.Add("Low attemptId coverage; model/tool retry lineage may be incomplete.");
        }

        if (quality.ToolCallIdCoverage < 0.5)
        {
            missing.Add("Low toolCallId coverage; tool-level causality may be incomplete.");
        }

        if (!events.Any(e => e.severity is RuntimeDiagnosticSeverity.Error or RuntimeDiagnosticSeverity.Critical))
        {
            missing.Add("No Error/Critical events in window; widen tail/time range or reproduce failure.");
        }

        return missing.ToArray();
    }

    private static EvidenceQuality BuildEvidenceQuality(IReadOnlyList<RuntimeDiagnosticEvent> events)
    {
        if (events.Count == 0)
        {
            return new EvidenceQuality(0.0, 0.0, 0.0, 0.0, 0.0);
        }

        static double Ratio(int part, int total) => total <= 0 ? 0.0 : (double)part / total;

        var total = events.Count;
        var runId = events.Count(e => !string.IsNullOrWhiteSpace(e.runId));
        var sessionId = events.Count(e => !string.IsNullOrWhiteSpace(e.sessionId));
        var attemptId = events.Count(e => !string.IsNullOrWhiteSpace(e.attemptId));
        var toolCallId = events.Count(e => !string.IsNullOrWhiteSpace(e.toolCallId));

        var runCoverage = Ratio(runId, total);
        var sessionCoverage = Ratio(sessionId, total);
        var attemptCoverage = Ratio(attemptId, total);
        var toolCoverage = Ratio(toolCallId, total);

        var score = (runCoverage * 0.35)
                    + (sessionCoverage * 0.35)
                    + (attemptCoverage * 0.15)
                    + (toolCoverage * 0.15);

        return new EvidenceQuality(score, runCoverage, sessionCoverage, attemptCoverage, toolCoverage);
    }

    private sealed record DiagnoseLogsReport(
        string summary,
        string? rootCause,
        double confidence,
        object[] errorCodes,
        object? primaryEvidence,
        object[] timeline,
        object[] missingSignals,
        object evidenceQuality,
        string[] nextChecks);

    private sealed record EvidenceQuality(
        double Score,
        double RunIdCoverage,
        double SessionIdCoverage,
        double AttemptIdCoverage,
        double ToolCallIdCoverage);
}

