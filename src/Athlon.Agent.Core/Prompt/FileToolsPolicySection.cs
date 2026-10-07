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



        var readOnlyMode = PromptModeHelper.IsAskMode(context) || PromptModeHelper.IsPlanMode(context);

        builder.AppendLine("File tools:");

        builder.AppendLine("- Search before large reads: when grep_files or glob_files are advertised, locate with them, then file_read in chunks.");

        if (!readOnlyMode)

        {

            builder.AppendLine("- Editing: when file_write, file_edit, or apply_patch are advertised, prefer apply_patch for large replacements; follow each write tool's description for retries and payload size.");

        }



        builder.AppendLine(readOnlyMode

            ? "- Paths from listing/search tools are exact on-disk names. Copy them character-for-character into available file tools."

            : "- Paths from listing/search tools are exact on-disk names. Copy them character-for-character into subsequent file and shell tools.");

        builder.AppendLine("- Never insert spaces between Latin letters and CJK characters inside a filename (e.g. disk has GMT沙盒AI演示.mp4 — not \"GMT 沙盒 AI 演示.mp4\").");



        builder.AppendLine();

    }

}

