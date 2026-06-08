using System.Text;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Memory;
using Athlon.Agent.Core.Prompt;

namespace Athlon.Agent.Infrastructure.Memory;

/// <summary>
/// Injects the curated MEMORY.md content into the system prompt before each reasoning iteration.
/// Only when memory is enabled and MEMORY.md is non-empty.
/// </summary>
public sealed class MemoryPromptContributor(ILongTermMemory longTermMemory, AppSettings settings) : IPreReasoningPromptContributor
{
    private readonly MemorySettings _cfg = settings.Memory;

    public int Priority => 40; // after workspace files, before skills

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (!_cfg.Enabled)
            return;

        // Read synchronously — this runs in the prompt builder hot path.
        // The file read is fast (MEMORY.md is small, ~4000 tokens).
        var memoryContent = longTermMemory.ReadCuratedAsync().GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(memoryContent))
            return;

        builder.AppendLine();
        builder.AppendLine("## Long-Term Memory");
        builder.AppendLine();
        builder.AppendLine("Below is the consolidated long-term memory from previous sessions. Use it to recall user preferences, past decisions, and persistent context.");
        builder.AppendLine();
        builder.AppendLine("<long_term_memory>");
        builder.AppendLine(memoryContent.Trim());
        builder.AppendLine("</long_term_memory>");
        builder.AppendLine();
    }
}
