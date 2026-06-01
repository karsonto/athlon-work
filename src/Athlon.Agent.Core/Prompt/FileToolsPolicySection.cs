using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class FileToolsPolicySection : IEnvironmentPromptSection
{
    public int Order => 450;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        builder.AppendLine("File tools:");
        builder.AppendLine("- For large files, use grep_files or glob_files to locate content before file_read.");
        builder.AppendLine("- Use file_read with offset and limit to read in chunks; do not assume a single read covers the whole file.");
        builder.AppendLine("- When file_read returns truncated: true or a next_offset in the meta footer, continue with that offset.");
        builder.AppendLine("- file_read line output uses N| prefixes for display only; file_edit old_text must match disk content without those prefixes.");
        builder.AppendLine();
    }
}
