using System.Text;
using Athlon.Agent.Core.Debug;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class DebugRuntimeContextContributor(
    ISessionHarnessState harnessState,
    IDebugPhaseAccessor phaseAccessor) : IRuntimeContextContributor
{
    public int Priority => 5;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (harnessState.GetMode(context.Session.Id) != SessionAgentMode.Debug)
        {
            return;
        }

        var run = phaseAccessor.GetActiveRun(context.Session.Id);
        if (run is null)
        {
            builder.AppendLine("Debug mode is active but no debug run is loaded yet.");
            return;
        }

        builder.AppendLine("Active debug run:");
        builder.AppendLine($"- run_id: {run.Id}");
        builder.AppendLine($"- phase: {run.Phase}");
        builder.AppendLine($"- log_path: {run.LogPath}");
        if (!string.IsNullOrWhiteSpace(run.BugDescription))
        {
            builder.AppendLine($"- bug: {run.BugDescription}");
        }

        if (run.Hypotheses.Count > 0)
        {
            builder.AppendLine("- hypotheses:");
            foreach (var hypothesis in run.Hypotheses)
            {
                builder.AppendLine($"  - {hypothesis.Id}: {hypothesis.Summary} ({hypothesis.Status})");
            }
        }

        builder.AppendLine();
        builder.AppendLine(DebugPhaseInstructions.For(run.Phase, run.LogPath));
        builder.AppendLine();
    }
}

internal static class DebugPhaseInstructions
{
    internal static string For(DebugPhase phase, string logPath) => phase switch
    {
        DebugPhase.Hypothesize =>
            "Phase Hypothesize: explore with read/grep tools only. Output 3-5 hypotheses as standalone lines `- H1: ...`. Do not edit files. Do not claim a root cause.",
        DebugPhase.Instrument =>
            "Phase Instrument: add minimal log probes via file_edit/file_write (user approves diffs). "
            + $"Each probe appends JSONL to `{logPath}`. End with a numbered `## Repro steps` section. Do not fix the bug yet.",
        DebugPhase.Analyze =>
            "Phase Analyze: call diagnose_logs FIRST. You may state a root cause only if the diagnostic report contains matching evidence you cite. "
            + "If the log file is missing, empty, or has no matching entries, say evidence is insufficient — do not guess a root cause and do not propose a fix.",
        DebugPhase.Fix =>
            "Phase Fix: apply the smallest correct code change via file_edit for the log-supported root cause only. Do not add new probes. Do not invent causes not backed by logs.",
        DebugPhase.Cleanup =>
            "Phase Cleanup: remove all `#region athlon-debug` probes via file_edit. Leave the functional fix intact.",
        DebugPhase.AwaitRepro =>
            "Phase AwaitRepro: the user is adding context while reproducing. Update repro understanding if needed. "
            + "Do not edit files, do not call diagnose_logs, and do not state a root cause until they mark the bug reproduced.",
        DebugPhase.AwaitFixConfirm =>
            "Phase AwaitFixConfirm: the user is reviewing the analysis. Treat extra messages as questions or extra context. "
            + "Do not edit files and do not start a fix until they confirm. If logs were empty, say evidence is still insufficient.",
        DebugPhase.AwaitVerify =>
            "Phase AwaitVerify: the user is verifying the fix. Treat extra messages as verification notes. "
            + "Do not edit files and do not change the debug phase.",
        _ => $"Phase {phase}: wait for user action. Do not edit files or conclude a root cause without log evidence."
    };
}
