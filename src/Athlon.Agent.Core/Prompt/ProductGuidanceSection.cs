using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class ProductGuidanceSection : IEnvironmentPromptSection
{
    public int Order => 700;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        builder.AppendLine("When context grows large, history is auto-compressed; full transcripts are kept under the session transcripts folder.");
    }
}
