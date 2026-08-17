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
            "Phase Hypothesize: explore with read/grep tools only. Output 3-5 hypotheses as `- H1: ...` lines. Do not edit files.",
        DebugPhase.Instrument =>
            "Phase Instrument: add minimal log probes via file_edit/file_write (user approves diffs). "
            + $"Each probe appends JSONL to `{logPath}`. End with a numbered `## Repro steps` section.",
        DebugPhase.Analyze =>
            "Phase Analyze: call debug_read_logs first. Use runtime evidence to mark hypotheses supported/refuted and state the root cause. Do not edit files yet.",
        DebugPhase.Fix =>
            "Phase Fix: apply the smallest correct code change via file_edit. Do not add new probes.",
        DebugPhase.Cleanup =>
            "Phase Cleanup: remove all `#region athlon-debug` probes via file_edit. Leave the functional fix intact.",
        _ => $"Phase {phase}: wait for user action."
    };
}
