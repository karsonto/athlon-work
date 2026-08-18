using System.Text;
using Athlon.Agent.Core.Harness;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Prompt;

public sealed class DebugModePromptSection : IEnvironmentPromptSection
{
    public string Name => "session:debug-mode";

    public int Order => PromptSectionBands.Mode + 1;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (context.AgentMode != SessionAgentMode.Debug)
        {
            return;
        }

        builder.AppendLine("Debug mode workflow:");
        builder.AppendLine("- You are debugging a reproducible bug using runtime evidence, not guesses.");
        builder.AppendLine("- Follow the active debug phase instructions injected in runtime context.");
        builder.AppendLine("- Evidence gate: never claim a root cause and never apply a functional fix until debug_read_logs returned matching hits. Empty logs are not evidence.");
        builder.AppendLine("- Hypothesize before editing; instrument before fixing; clean up probes before finishing.");
        builder.AppendLine("- Hypotheses MUST be standalone markdown lines of the form `- H1: …` (then H2, H3, …).");
        builder.AppendLine("- Instrument MUST end with a `## Repro steps` section listing numbered user actions.");
        builder.AppendLine("- Log probes must append one JSON object per line to the active debug log path.");
        builder.AppendLine("- Wrap each probe in `#region athlon-debug Hn` … `#endregion athlon-debug Hn` so cleanup is reliable.");
        builder.AppendLine("- JSON shape: {\"ts\":\"ISO-8601\",\"runId\":\"...\",\"hypothesisId\":\"H1\",\"location\":\"File.ext:line\",\"message\":\"...\",\"data\":{...}}");
        builder.AppendLine();
    }
}
