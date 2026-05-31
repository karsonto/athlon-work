using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class ProductGuidanceSection : IEnvironmentPromptSection
{
    public int Order => 700;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        builder.AppendLine("When context grows large, history is auto-compressed; full transcripts are kept under the session transcripts folder.");
        builder.AppendLine();
        AppendMermaidGuidance(builder);
    }

    private static void AppendMermaidGuidance(StringBuilder builder)
    {
        builder.AppendLine("Mermaid diagrams in chat:");
        builder.AppendLine("- When a diagram clarifies the answer better than prose alone, include one or more fenced ```mermaid code blocks (e.g. flowchart, sequenceDiagram, stateDiagram-v2, classDiagram, erDiagram, gantt).");
        builder.AppendLine("- Prefer Mermaid for: request/API flows, multi-step processes, component or deployment topology, state transitions, timelines, and decision branches.");
        builder.AppendLine("- Skip diagrams for simple factual answers, short lists, or when the user only wants code/text.");
        builder.AppendLine("- Keep each diagram focused; use multiple small diagrams instead of one huge chart.");
        builder.AppendLine("- In Athlon Agent the chat shows Mermaid as source code, not inline graphics. Tell the user they can right-click the message and choose \"查看 Mermaid 图表\" for an offline rendered preview.");
        builder.AppendLine("- Do not claim an inline image is visible unless you also describe the structure in text.");
    }
}
