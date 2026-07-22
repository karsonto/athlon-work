using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class FileToolsPolicySection : IEnvironmentPromptSection
{
    public int Order => 450;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        if (PromptModeHelper.IsChatOnly(context))
        {
            return;
        }

        builder.AppendLine("File tools:");
        builder.AppendLine("- Search: simple strings → grep_files (literal); patterns or class names → grep_files with regex true; find files by name → glob_files.");
        builder.AppendLine("- For large files, use grep_files or glob_files to locate content before file_read.");
        builder.AppendLine("- grep_files uses literal matching by default; set regex to true for .NET regular expressions.");
        builder.AppendLine("- Use file_read with 1-based start_line and end_line to read in chunks; do not assume a single read covers the whole file.");
        builder.AppendLine("- When file_read returns truncated: true or next_start_line, continue from that 1-based line.");

        if (!PromptModeHelper.IsAskMode(context) && !PromptModeHelper.IsPlanMode(context))
        {
            builder.AppendLine("- file_read line output uses N| prefixes for display only; file_edit old_text must match disk content without those prefixes.");
            builder.AppendLine("- Editing: prefer apply_patch when old_text would span 5+ lines; exact matching is fragile for large blocks.");
            builder.AppendLine("- Editing: if file_edit fails, re-read the file and retry once; after two failures switch to apply_patch or file_write (small files only). Never retry the same old_text a third time.");
            builder.AppendLine("- file_write requires non-empty `content` as a JSON string with the full file body; do not omit it or pass null/empty.");
            builder.AppendLine("- For file_write, put `path` before `content` in the arguments object so streaming truncation still preserves the path.");
            builder.AppendLine("- Do not file_write large HTML/JS in one shot; write a short skeleton then extend with file_edit or apply_patch. Keep each content payload small so the JSON closes completely.");
        }
        builder.AppendLine(PromptModeHelper.IsAskMode(context) || PromptModeHelper.IsPlanMode(context)
            ? "- Paths from file_list, glob_files, or grep_files are exact on-disk names. Copy them character-for-character into available file tools."
            : "- Paths from file_list, glob_files, or grep_files are exact on-disk names. Copy them character-for-character into file_read, file_write, file_edit, apply_patch, and execute_command.");
        builder.AppendLine("- Never insert spaces between Latin letters and CJK characters inside a filename (e.g. disk has GMT沙盒AI演示.mp4 — not \"GMT 沙盒 AI 演示.mp4\").");
        builder.AppendLine();
    }
}
