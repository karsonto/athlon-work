using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;
using Athlon.Agent.Infrastructure.Knowledge;

namespace Athlon.Agent.Tests;

public sealed class KnowledgeOcrTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Attachments", name);

    [Fact]
    public void Parse_MapsPageHeaders()
    {
        var parsed = KnowledgeOcrResponseParser.Parse(
            """
            ### Page 2
            second

            ### Page 4
            fourth
            """,
            [2, 4]);

        Assert.Equal("second", parsed[2]);
        Assert.Equal("fourth", parsed[4]);
    }

    [Fact]
    public void Parse_SinglePageFallback_WithoutHeaders()
    {
        var parsed = KnowledgeOcrResponseParser.Parse("plain text only", [7]);
        Assert.Equal("plain text only", parsed[7]);
    }

    [Fact]
    public async Task VisionChatKnowledgeOcr_SendsImagesAndParsesPages()
    {
        var client = new CapturingModelClient(
            """
            ### Page 1
            alpha

            ### Page 2
            beta
            """);
        var ocr = new VisionChatKnowledgeOcr(client, new NoOpLogger());
        var temp = Path.Combine(Path.GetTempPath(), "athlon-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var p1 = Path.Combine(temp, "1.jpg");
            var p2 = Path.Combine(temp, "2.jpg");
            await File.WriteAllBytesAsync(p1, [0xFF, 0xD8, 0xFF, 0xD9]);
            await File.WriteAllBytesAsync(p2, [0xFF, 0xD8, 0xFF, 0xD9]);

            var result = await ocr.RecognizePagesAsync(
            [
                new KnowledgeOcrPageImage(1, new ImageAttachment("1.jpg", "image/jpeg", LocalPath: p1)),
                new KnowledgeOcrPageImage(2, new ImageAttachment("2.jpg", "image/jpeg", LocalPath: p2))
            ]);

            Assert.Equal("alpha", result[1]);
            Assert.Equal("beta", result[2]);
            Assert.NotNull(client.LastRequest);
            Assert.False(client.LastRequest!.AllowToolCalls);
            var user = Assert.IsType<List<object>>(client.LastRequest.Messages.Last().Content);
            Assert.Equal(3, user.Count); // text + 2 images
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractAsync_WhenOcrDisabled_DoesNotCallOcr()
    {
        var settings = new AppSettings();
        settings.Knowledge.Ocr.Enabled = false;
        var ocr = new RecordingOcr();
        var extractor = new KnowledgeDocumentExtractor(settings, ocr, new NoOpLogger());

        var extracted = await extractor.ExtractAsync(Fixture("sample-3pages.pdf"));

        Assert.False(string.IsNullOrWhiteSpace(extracted.Text));
        Assert.Empty(ocr.BatchSizes);
    }

    [Fact]
    public async Task ExtractAsync_WhenOcrDisabled_EmptyPdf_UsesLegacyError()
    {
        var settings = new AppSettings();
        settings.Knowledge.Ocr.Enabled = false;
        var ocr = new RecordingOcr();
        var extractor = new KnowledgeDocumentExtractor(settings, ocr, new NoOpLogger());
        var path = Path.Combine(Path.GetTempPath(), "athlon-blank-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, CreateBlankPdfBytes(pageCount: 1));
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => extractor.ExtractAsync(path));
            Assert.Contains("扫描件", error.Message);
            Assert.DoesNotContain("视觉 OCR", error.Message);
            Assert.Empty(ocr.BatchSizes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExtractAsync_WhenOcrEnabled_TextOnlyPdf_DoesNotCallOcr()
    {
        var settings = new AppSettings();
        settings.Knowledge.Ocr.Enabled = true;
        var ocr = new RecordingOcr();
        var extractor = new KnowledgeDocumentExtractor(settings, ocr, new NoOpLogger());

        var extracted = await extractor.ExtractAsync(Fixture("sample-3pages.pdf"));

        Assert.Contains("# Page 1", extracted.Text);
        Assert.Empty(ocr.BatchSizes);
        Assert.DoesNotContain("ocr-page-", extracted.Text);
    }

    [Fact]
    public async Task ExtractAsync_WhenOcrEnabled_BlankPages_FallbackFullPageOcr_BatchesBySize()
    {
        var settings = new AppSettings();
        settings.Knowledge.Ocr.Enabled = true;
        settings.Knowledge.Ocr.BatchSize = 2;
        var ocr = new RecordingOcr();
        var extractor = new KnowledgeDocumentExtractor(settings, ocr, new NoOpLogger());
        var path = Path.Combine(Path.GetTempPath(), "athlon-blank3-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, CreateBlankPdfBytes(pageCount: 3));
        try
        {
            var extracted = await extractor.ExtractAsync(path);

            Assert.Contains("# Page 1", extracted.Text);
            Assert.Contains("ocr-page-", extracted.Text);
            Assert.Equal([2, 1], ocr.BatchSizes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExtractAsync_WhenOcrEnabled_AndOcrEmpty_UsesVisionError()
    {
        var settings = new AppSettings();
        settings.Knowledge.Ocr.Enabled = true;
        var ocr = new EmptyOcr();
        var extractor = new KnowledgeDocumentExtractor(settings, ocr, new NoOpLogger());
        var path = Path.Combine(Path.GetTempPath(), "athlon-blank-emptyocr-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, CreateBlankPdfBytes(pageCount: 1));
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => extractor.ExtractAsync(path));
            Assert.Contains("视觉 OCR", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExtractAsync_WhenOcrEnabled_DefaultBatchSizeThree_OnBlankPages()
    {
        var settings = new AppSettings();
        settings.Knowledge.Ocr.Enabled = true;
        Assert.Equal(3, settings.Knowledge.Ocr.BatchSize);
        var ocr = new RecordingOcr();
        var extractor = new KnowledgeDocumentExtractor(settings, ocr, new NoOpLogger());
        var path = Path.Combine(Path.GetTempPath(), "athlon-blank-batch3-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, CreateBlankPdfBytes(pageCount: 3));
        try
        {
            await extractor.ExtractAsync(path);
            Assert.Equal([3], ocr.BatchSizes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExtractAsync_WhenOcrEnabled_MergesTextAndOcrOnSamePage()
    {
        // Blank page → full-page OCR fallback; RecordingOcr returns text keyed by slot.
        // Also verify text-only pages keep # Page markers without forcing MinChars.
        var settings = new AppSettings();
        settings.Knowledge.Ocr.Enabled = true;
        var ocr = new RecordingOcr();
        var extractor = new KnowledgeDocumentExtractor(settings, ocr, new NoOpLogger());
        var path = Path.Combine(Path.GetTempPath(), "athlon-blank-merge-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, CreateBlankPdfBytes(pageCount: 1));
        try
        {
            var extracted = await extractor.ExtractAsync(path);
            Assert.Contains("# Page 1", extracted.Text);
            Assert.Contains("ocr-page-1", extracted.Text);
            // Single page block (no second # Page for OCR)
            Assert.Single(extracted.Text.Split("# Page ", StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Minimal multi-page PDF with no extractable text layer (PdfPig only; no rasterization).</summary>
    private static byte[] CreateBlankPdfBytes(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        var objects = new List<string>
        {
            "1 0 obj<< /Type /Catalog /Pages 2 0 R >>endobj\n"
        };

        var kids = new List<string>();
        var nextId = 3;
        for (var i = 0; i < pageCount; i++)
        {
            var pageId = nextId++;
            var contentId = nextId++;
            kids.Add($"{pageId} 0 R");
            objects.Add($"{pageId} 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents {contentId} 0 R /Resources << >> >>endobj\n");
            objects.Add($"{contentId} 0 obj<< /Length 0 >>stream\nendstream\nendobj\n");
        }

        objects.Insert(1, $"2 0 obj<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {pageCount} >>endobj\n");

        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        writer.Write("%PDF-1.4\n");
        writer.Flush();
        var offsets = new List<long> { 0 };
        foreach (var obj in objects)
        {
            offsets.Add(stream.Position);
            writer.Write(obj);
            writer.Flush();
        }

        var xref = stream.Position;
        writer.Write($"xref\n0 {offsets.Count}\n");
        writer.Write("0000000000 65535 f \n");
        for (var i = 1; i < offsets.Count; i++)
        {
            writer.Write($"{offsets[i]:D10} 00000 n \n");
        }

        writer.Write($"trailer<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        writer.Flush();
        return stream.ToArray();
    }

    private sealed class RecordingOcr : IKnowledgePageOcr
    {
        public List<int> BatchSizes { get; } = [];

        public Task<IReadOnlyDictionary<int, string>> RecognizePagesAsync(
            IReadOnlyList<KnowledgeOcrPageImage> pages,
            CancellationToken cancellationToken = default)
        {
            BatchSizes.Add(pages.Count);
            var map = pages.ToDictionary(
                page => page.PageNumber,
                page => $"ocr-page-{page.PageNumber}");
            return Task.FromResult<IReadOnlyDictionary<int, string>>(map);
        }
    }

    private sealed class EmptyOcr : IKnowledgePageOcr
    {
        public Task<IReadOnlyDictionary<int, string>> RecognizePagesAsync(
            IReadOnlyList<KnowledgeOcrPageImage> pages,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    }

    private sealed class CapturingModelClient(string content) : IAgentModelClient
    {
        public AgentModelRequest? LastRequest { get; private set; }

        public Task<AgentModelResponse> CompleteAsync(
            AgentModelRequest request,
            Func<string, Task>? onTextDelta = null,
            Func<string, Task>? onReasoningDelta = null,
            Func<StreamingToolCallDelta, Task>? onToolCallDelta = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new AgentModelResponse(content, Array.Empty<AgentToolCall>()));
        }
    }

    private sealed class NoOpLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
