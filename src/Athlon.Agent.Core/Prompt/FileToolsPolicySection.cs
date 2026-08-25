using System.Text;



namespace Athlon.Agent.Core.Prompt;



/// <summary>

/// Cross-tool file habits. Per-tool contracts live in <see cref="ToolDefinition.Description"/>.

/// </summary>

public sealed class FileToolsPolicySection : IEnvironmentPromptSection

{

    public string Name => "tool:files";



    public int Order => PromptSectionBands.ToolGuidanceStart;



    public void Append(StringBuilder builder, EnvironmentPromptContext context)

    {

        if (PromptModeHelper.IsChatOnly(context))

        {

            return;

        }



        var hasSearch = PromptModeHelper.HasAny(context, "grep_files", "glob_files");

        var hasRead = PromptModeHelper.HasTool(context, "file_read");

        var hasList = PromptModeHelper.HasTool(context, "file_list");

        var hasWrite = PromptModeHelper.HasAny(context, "file_write", "file_edit", "apply_patch");

        if (!hasSearch && !hasRead && !hasList && !hasWrite)

        {

            return;

        }



        builder.AppendLine("File tools:");

        if (hasSearch && hasRead)

        {

            builder.AppendLine("- Search before large reads: locate with grep_files / glob_files, then file_read in chunks.");

        }

        else if (hasSearch)

        {

            builder.AppendLine("- Use grep_files / glob_files (when advertised) to locate content or files by name.");

        }

        else if (hasRead)

        {

            builder.AppendLine("- Use file_read in chunks; follow each tool description for truncation / next_start_line.");

        }



        if (hasWrite && !PromptModeHelper.IsAskMode(context) && !PromptModeHelper.IsPlanMode(context))

        {

            builder.AppendLine("- Editing: prefer apply_patch for large replacements; follow each write tool's description for retries and payload size.");

        }



        if (hasList || hasSearch || hasRead)

        {

            builder.AppendLine(PromptModeHelper.IsAskMode(context) || PromptModeHelper.IsPlanMode(context)

                ? "- Paths from listing/search tools are exact on-disk names. Copy them character-for-character into available file tools."

                : "- Paths from listing/search tools are exact on-disk names. Copy them character-for-character into subsequent file and shell tools.");

            builder.AppendLine("- Never insert spaces between Latin letters and CJK characters inside a filename (e.g. disk has GMT沙盒AI演示.mp4 — not \"GMT 沙盒 AI 演示.mp4\").");

        }



        builder.AppendLine();

    }

}

