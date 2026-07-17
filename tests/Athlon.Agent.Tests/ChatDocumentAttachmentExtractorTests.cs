using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure;
using Athlon.Agent.Infrastructure.Knowledge;

namespace Athlon.Agent.Tests;

public sealed class ChatDocumentAttachmentExtractorTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Attachments", name);

    [Fact]
    public void Formatter_JoinsUserTextAndExtractionsDocuments()
    {
        var joined = ChatDocumentAttachmentFormatter.JoinUserInputWithExtractedDocuments(
            "请总结",
            [
                new ChatDocumentExtractionResult(
                    "a.pdf",
                    "page text",
                    Array.Empty<ImageAttachment>())
            ]);

        Assert.Contains("请总结", joined);
        Assert.Contains("文件 \"a.pdf\" 的正文已提取，并附带全部页图:", joined);
        Assert.Contains("page text", joined);
    }

    [Fact]
    public async Task ExtractAllVisualAsync_Pdf_RendersAllPages()
    {
        var path = Fixture("sample-3pages.pdf");
        Assert.True(File.Exists(path), $"Missing fixture: {path}");

        var extractor = new ChatDocumentAttachmentExtractor();
        var result = await extractor.ExtractAllVisualAsync(path);

        Assert.Equal("sample-3pages.pdf", result.SourceFileName);
        Assert.Contains("Hello Page 1", result.TextContent);
        Assert.Contains("Hello Page 2", result.TextContent);
        Assert.Contains("Hello Page 3", result.TextContent);
        Assert.Equal(3, result.VisualAttachments.Count);
        Assert.All(result.VisualAttachments, image =>
        {
            Assert.Equal("image/jpeg", image.MimeType);
            Assert.False(string.IsNullOrWhiteSpace(image.LocalPath));
            Assert.True(File.Exists(image.LocalPath!));
            Assert.True(new FileInfo(image.LocalPath!).Length > 0);
        });
    }

    [Fact]
    public async Task ExtractAllVisualAsync_Docx_ExtractsAllEmbeddedImages()
    {
        var path = Fixture("sample-with-image.docx");
        Assert.True(File.Exists(path), $"Missing fixture: {path}");

        var extractor = new ChatDocumentAttachmentExtractor();
        var result = await extractor.ExtractAllVisualAsync(path);

        Assert.Contains("Docx body text", result.TextContent);
        Assert.Equal(2, result.VisualAttachments.Count);
        Assert.All(result.VisualAttachments, image =>
            Assert.Equal("image/png", image.MimeType));
    }

    [Fact]
    public async Task ExtractAllVisualAsync_Pptx_ExtractsEmbeddedImages()
    {
        var path = Fixture("sample-with-image.pptx");
        Assert.True(File.Exists(path), $"Missing fixture: {path}");

        var extractor = new ChatDocumentAttachmentExtractor();
        var result = await extractor.ExtractAllVisualAsync(path);

        Assert.Contains("Slide one text", result.TextContent);
        Assert.Single(result.VisualAttachments);
        Assert.Equal("image/png", result.VisualAttachments[0].MimeType);
    }

    [Fact]
    public async Task ExtractAllVisualAsync_PlainText_HasNoVisuals()
    {
        var path = Fixture("sample.txt");
        Assert.True(File.Exists(path), $"Missing fixture: {path}");

        var extractor = new ChatDocumentAttachmentExtractor();
        var result = await extractor.ExtractAllVisualAsync(path);

        Assert.Contains("plain text attachment", result.TextContent);
        Assert.Empty(result.VisualAttachments);
    }

    [Fact]
    public void IsLegacyPresentation_RejectsPpt()
    {
        var extractor = new ChatDocumentAttachmentExtractor();
        Assert.True(extractor.IsLegacyPresentation("deck.ppt"));
        Assert.False(extractor.IsLegacyPresentation("deck.pptx"));
    }

    [Fact]
    public async Task KnowledgeDocumentExtractor_StillExtractsMarkdown()
    {
        var temp = Path.Combine(Path.GetTempPath(), "athlon-kd-" + Guid.NewGuid().ToString("N") + ".md");
        await File.WriteAllTextAsync(temp, "# Title\n\nbody");
        try
        {
            var knowledge = new KnowledgeDocumentExtractor();
            var extracted = await knowledge.ExtractAsync(temp);
            Assert.Contains("body", extracted.Text);
            Assert.Equal(Path.GetFileNameWithoutExtension(temp), extracted.Title);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
