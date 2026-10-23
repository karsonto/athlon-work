using Athlon.Agent.Core.RuntimeDiagnostics;

namespace Athlon.Agent.Core.Compaction;

/// <summary>Structured compaction skip/degrade/success decisions for app logs and runtime events.</summary>
public static class CompactionDecisionLog
{
    public const string EventType = "compaction.decision";

    public static void Write(
        IAppLogger logger,
        IRuntimeDiagnosticEventSink? sink,
        string? sessionId,
        string outcome,
        string reason,
        ContextPressureLevel? pressure = null,
        int? cutoff = null,
        int? tailMessages = null,
        int? tokensBefore = null,
        int? projectedAfter = null,
        int? keepBudget = null)
    {
        logger.Information(
            "compaction.decision outcome={Outcome} reason={Reason} pressure={Pressure} cutoff={Cutoff} tailMessages={TailMessages} tokensBefore={TokensBefore} projectedAfter={ProjectedAfter} keepBudget={KeepBudget}",
            outcome,
            reason,
            pressure?.ToString() ?? "-",
            cutoff?.ToString() ?? "-",
            tailMessages?.ToString() ?? "-",
            tokensBefore?.ToString() ?? "-",
            projectedAfter?.ToString() ?? "-",
            keepBudget?.ToString() ?? "-");

        if (sink is null)
        {
            return;
        }

        var message =
            $"outcome={outcome}; reason={reason}; pressure={pressure}; cutoff={cutoff}; tailMessages={tailMessages}; " +
            $"tokensBefore={tokensBefore}; projectedAfter={projectedAfter}; keepBudget={keepBudget}";

        var evt = new RuntimeDiagnosticEvent(
            eventId: "",
            ts: default,
            sequence: 0,
            sessionId: sessionId,
            runId: null,
            turnId: null,
            attemptId: null,
            parentAttemptId: null,
            toolCallId: null,
            messageId: null,
            component: RuntimeDiagnosticComponent.Compaction,
            phase: RuntimeDiagnosticPhase.Persist,
            eventType: EventType,
            severity: outcome is "skipped" or "degraded" or "template"
                ? RuntimeDiagnosticSeverity.Warning
                : RuntimeDiagnosticSeverity.Info,
            message: message);

        _ = sink.EnqueueAsync(evt, CancellationToken.None);
    }
}
