using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class EncodingPolicySection : IEnvironmentPromptSection
{
    public int Order => 210;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        builder.AppendLine("Encoding and locale:");
        builder.AppendLine("- Use UTF-8 for all file content, patches, command output, and text you write unless a tool result explicitly states another encoding.");
        builder.AppendLine("- Assume workspace files and tool I/O are UTF-8; do not convert Chinese or other non-ASCII text to escape sequences or legacy code pages.");
        builder.AppendLine();
    }
}
