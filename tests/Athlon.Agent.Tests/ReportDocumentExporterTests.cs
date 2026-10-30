using System.IO.Compression;
using System.Text;
using Athlon.Agent.App.Services;
using Athlon.Agent.Core;

namespace Athlon.Agent.Tests;

public sealed class ReportDocumentExporterTests : IDisposable
{
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "athlon-report-" + Guid.NewGuid().ToString("N"));

    public ReportDocumentExporterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Rewrite_keeps_citations_that_are_not_saved_browser_frames()
    {
        var markdown = """
            ![ok](screenshot:browser-frame-ok.png)
            ![missing](screenshot:browser-frame-missing.png)
            ![other](screenshot:photo.png)
            """;

        var rewritten = ReportDocumentExporter.RewriteScreenshotCitations(
            markdown,
            SampleImages(),
            "报告-images");

        Assert.Contains("](报告-images/browser-frame-ok.png)", rewritten, StringComparison.Ordinal);
        Assert.Contains("(screenshot:browser-frame-missing.png)", rewritten, StringComparison.Ordinal);
        Assert.Contains("(screenshot:photo.png)", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteMarkdown_puts_cited_frames_beside_the_report()
    {
        var path = Path.Combine(_root, "报告.md");
        ReportDocumentExporter.WriteMarkdown(path, ReportMarkdown(), SampleImages());

        var saved = File.ReadAllText(path);
        Assert.Contains("](报告-images/browser-frame-ok.png)", saved, StringComparison.Ordinal);
        Assert.Contains("(screenshot:browser-frame-missing.png)", saved, StringComparison.Ordinal);
        var imagePath = Path.Combine(_root, "报告-images", "browser-frame-ok.png");
        Assert.Equal(Convert.FromBase64String(PngBase64), File.ReadAllBytes(imagePath));
        Assert.False(File.Exists(Path.Combine(_root, "报告-images", "photo.png")));
    }

    [Fact]
    public void WriteDocx_embeds_the_cited_frame()
    {
        var path = Path.Combine(_root, "报告.docx");
        ReportDocumentExporter.WriteDocx(path, ReportMarkdown(), SampleImages());

        using var zip = ZipFile.OpenRead(path);
        var media = zip.Entries.Single(entry =>
            entry.FullName.StartsWith("media/", StringComparison.Ordinal)
            || entry.FullName.Contains("/media/", StringComparison.Ordinal));
        using var mediaStream = media.Open();
        using var buffer = new MemoryStream();
        mediaStream.CopyTo(buffer);
        Assert.Equal(Convert.FromBase64String(PngBase64), buffer.ToArray());

        var document = zip.GetEntry("word/document.xml");
        Assert.NotNull(document);
        using var documentStream = document.Open();
        using var reader = new StreamReader(documentStream, Encoding.UTF8);
        var xml = reader.ReadToEnd();
        Assert.Contains("Quarterly", xml, StringComparison.Ordinal);
        Assert.Contains("r:embed=", xml, StringComparison.Ordinal);
    }

    private static string ReportMarkdown() => """
        # Quarterly

        - first

        ```
        code
        ```

        ![ok](screenshot:browser-frame-ok.png)

        ![missing](screenshot:browser-frame-missing.png)
        """;

    private static ImageAttachment[] SampleImages() =>
    [
        new("browser-frame-ok.png", "image/png", "data:image/png;base64," + PngBase64),
        new("photo.png", "image/png", "data:image/png;base64," + PngBase64)
    ];
}
