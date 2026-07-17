namespace Athlon.Agent.Core;

public sealed record ChatDocumentExtractionResult(
    string SourceFileName,
    string TextContent,
    IReadOnlyList<ImageAttachment> VisualAttachments);

public interface IChatDocumentAttachmentExtractor
{
    bool IsSupportedDocument(string path);
    bool IsLegacyPresentation(string path);
    bool IsImageFile(string path);
    Task<ChatDocumentExtractionResult> ExtractAllVisualAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public static class ChatDocumentAttachmentFormatter
{
    public static string FormatExtractedDocument(ChatDocumentExtractionResult result)
    {
        var name = string.IsNullOrWhiteSpace(result.SourceFileName) ? "attachment" : result.SourceFileName;
        return $"文件 \"{name}\" 的正文已提取，并附带全部页图:\n```\n{result.TextContent}\n```";
    }

    public static string JoinUserInputWithExtractedDocuments(
        string userText,
        IReadOnlyList<ChatDocumentExtractionResult> results)
    {
        var segments = new List<string>();
        if (!string.IsNullOrWhiteSpace(userText))
        {
            segments.Add(userText.Trim());
        }

        foreach (var result in results)
        {
            segments.Add(FormatExtractedDocument(result));
        }

        return string.Join("\n\n", segments);
    }
}
