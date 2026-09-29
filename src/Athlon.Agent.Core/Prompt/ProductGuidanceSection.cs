using System.Text;

namespace Athlon.Agent.Core.Prompt;

public sealed class ProductGuidanceSection : IEnvironmentPromptSection
{
    public string Name => "product:guidance";

    public int Order => PromptSectionBands.Product;

    public void Append(StringBuilder builder, EnvironmentPromptContext context)
    {
        builder.AppendLine("When context grows large, history is auto-compressed. The summary is an outline. Recover original messages with history_list_transcripts, history_read_transcript, and history_search_transcripts. Recover truncated tool output with history_read_evicted. Keep the task skeleton with session_note_append and read it with session_note_read. Call request_context_compact only after that note is written, when the current phase can close.");
    }
}
