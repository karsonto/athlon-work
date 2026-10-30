using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Browser;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SkiaSharp;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using Table = Markdig.Extensions.Tables.Table;
using TableCell = Markdig.Extensions.Tables.TableCell;
using TableRow = Markdig.Extensions.Tables.TableRow;

namespace Athlon.Agent.App.Services;

/// <summary>
/// Saves one assistant report as Markdown plus cited screenshots, or as a single Word file.
/// </summary>
public static partial class ReportDocumentExporter
{
    private const long EmuPerInch = 914400;
    private const long MaxWidthEmu = (long)(6.5 * EmuPerInch);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    [GeneratedRegex(@"\(screenshot:([^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ScreenshotReference();

    public static void WriteMarkdown(string path, string markdown, IReadOnlyList<ImageAttachment> images)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var folderName = ImageFolderName(path);
        var cited = new List<ImageAttachment>();
        var rewritten = RewriteScreenshotCitations(markdown ?? string.Empty, images, folderName, cited);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, rewritten, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (cited.Count == 0 || string.IsNullOrEmpty(directory))
        {
            return;
        }

        var imageDirectory = Path.Combine(directory, folderName);
        Directory.CreateDirectory(imageDirectory);
        foreach (var image in cited)
        {
            var bytes = ReadImageBytes(image);
            if (bytes is null)
            {
                continue;
            }

            File.WriteAllBytes(Path.Combine(imageDirectory, image.FileName), bytes);
        }
    }

    public static void WriteDocx(string path, string markdown, IReadOnlyList<ImageAttachment> images)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        var writer = new DocxWriter(main, body, IndexScreenshots(images));
        foreach (var block in Markdown.Parse(markdown ?? string.Empty, Pipeline))
        {
            writer.AppendBlock(block, marker: null);
        }

        if (!body.Elements<Paragraph>().Any() && !body.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>().Any())
        {
            body.Append(new Paragraph());
        }

        main.Document.Save();
    }

    public static string RewriteScreenshotCitations(
        string markdown,
        IReadOnlyList<ImageAttachment> images,
        string imageDirectoryName,
        ICollection<ImageAttachment>? cited = null)
    {
        var index = IndexScreenshots(images);
        var written = new HashSet<string>(StringComparer.Ordinal);
        return ScreenshotReference().Replace(markdown ?? string.Empty, match =>
        {
            var name = match.Groups[1].Value;
            if (!index.TryGetValue(name, out var image) || ReadImageBytes(image) is null)
            {
                return match.Value;
            }

            if (cited is not null && written.Add(name))
            {
                cited.Add(image);
            }

            return "(" + EscapeUrl(imageDirectoryName) + "/" + EscapeUrl(name) + ")";
        });
    }

    internal static byte[]? ReadImageBytes(ImageAttachment image)
    {
        if (!string.IsNullOrWhiteSpace(image.LocalPath) && File.Exists(image.LocalPath))
        {
            return File.ReadAllBytes(image.LocalPath);
        }

        var dataUrl = image.DataUrl;
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return null;
        }

        var comma = dataUrl.IndexOf(',');
        if (comma < 0 || comma == dataUrl.Length - 1)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Dictionary<string, ImageAttachment> IndexScreenshots(IReadOnlyList<ImageAttachment>? images)
    {
        var map = new Dictionary<string, ImageAttachment>(StringComparer.Ordinal);
        if (images is null)
        {
            return map;
        }

        foreach (var image in images)
        {
            if (string.IsNullOrWhiteSpace(image.FileName)
                || !image.FileName.StartsWith(BrowserFrameFiles.Prefix, StringComparison.Ordinal)
                || map.ContainsKey(image.FileName))
            {
                continue;
            }

            map[image.FileName] = image;
        }

        return map;
    }

    private static string ImageFolderName(string markdownPath)
    {
        var name = Path.GetFileNameWithoutExtension(markdownPath);
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var safe = builder.ToString().Trim().TrimEnd('.');
        return (string.IsNullOrWhiteSpace(safe) ? "report" : safe) + "-images";
    }

    private static string EscapeUrl(string value) =>
        value.Replace(" ", "%20", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);

    private sealed class DocxWriter(
        MainDocumentPart main,
        Body body,
        Dictionary<string, ImageAttachment> images)
    {
        private uint _drawingId = 1;

        public void AppendBlock(Block block, string? marker)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    AppendInlineParagraphs(heading.Inline, marker, heading.Level);
                    break;
                case ParagraphBlock paragraph:
                    AppendInlineParagraphs(paragraph.Inline, marker, headingLevel: 0);
                    break;
                case FencedCodeBlock or CodeBlock:
                    body.Append(CodeParagraph(((LeafBlock)block).Lines.ToString(), marker));
                    break;
                case ListBlock list:
                    AppendList(list);
                    break;
                case QuoteBlock quote:
                    foreach (var child in quote)
                    {
                        AppendBlock(child, marker);
                        marker = null;
                    }

                    break;
                case Table table:
                    foreach (var row in table.OfType<TableRow>())
                    {
                        var cells = row.OfType<TableCell>().Select(PlainText).Where(static text => text.Length > 0);
                        body.Append(TextParagraph(string.Join(" | ", cells), marker, headingLevel: 0));
                        marker = null;
                    }

                    break;
                case LeafBlock leaf:
                    var text = leaf.Lines.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        body.Append(TextParagraph(text, marker, headingLevel: 0));
                    }

                    break;
                case ContainerBlock container:
                    foreach (var child in container)
                    {
                        AppendBlock(child, marker);
                        marker = null;
                    }

                    break;
            }
        }

        private void AppendList(ListBlock list)
        {
            var number = 1;
            if (list.IsOrdered && !string.IsNullOrWhiteSpace(list.OrderedStart)
                && int.TryParse(list.OrderedStart, out var start))
            {
                number = start;
            }

            foreach (var item in list.OfType<ListItemBlock>())
            {
                var marker = list.IsOrdered ? number + ". " : "• ";
                number++;
                var pending = marker;
                foreach (var child in item)
                {
                    AppendBlock(child, pending);
                    pending = null;
                }
            }
        }

        private void AppendInlineParagraphs(ContainerInline? inline, string? marker, int headingLevel)
        {
            var paragraph = NewParagraph(headingLevel);
            var hasContent = false;
            if (!string.IsNullOrEmpty(marker))
            {
                paragraph.Append(MakeRun(marker, headingLevel, bold: false, italic: false, code: false));
                hasContent = true;
            }

            for (var node = inline?.FirstChild; node is not null; node = node.NextSibling)
            {
                if (node is LinkInline { IsImage: true } image
                    && TryCreateImageParagraph(image, out var imageParagraph))
                {
                    if (hasContent)
                    {
                        body.Append(paragraph);
                        paragraph = NewParagraph(headingLevel);
                        hasContent = false;
                    }

                    body.Append(imageParagraph);
                    continue;
                }

                if (AppendInline(paragraph, node, headingLevel, bold: false, italic: false))
                {
                    hasContent = true;
                }
            }

            if (hasContent)
            {
                body.Append(paragraph);
            }
        }

        private bool AppendInline(Paragraph paragraph, Inline node, int headingLevel, bool bold, bool italic)
        {
            switch (node)
            {
                case LiteralInline literal:
                    paragraph.Append(MakeRun(literal.Content.ToString(), headingLevel, bold, italic, code: false));
                    return true;
                case CodeInline code:
                    paragraph.Append(MakeRun(code.Content, headingLevel, bold, italic, code: true));
                    return true;
                case EmphasisInline emphasis:
                    var emphasizeBold = bold || emphasis.DelimiterCount >= 2;
                    var emphasizeItalic = italic || emphasis.DelimiterCount == 1;
                    var any = false;
                    for (var child = emphasis.FirstChild; child is not null; child = child.NextSibling)
                    {
                        any |= AppendInline(paragraph, child, headingLevel, emphasizeBold, emphasizeItalic);
                    }

                    return any;
                case LineBreakInline:
                    paragraph.Append(new Run(new Break()));
                    return true;
                case LinkInline link:
                    var label = PlainText(link);
                    paragraph.Append(MakeRun(
                        string.IsNullOrEmpty(label) ? link.Url ?? string.Empty : label,
                        headingLevel,
                        bold,
                        italic,
                        code: false));
                    return true;
                case ContainerInline container:
                    var nested = false;
                    for (var child = container.FirstChild; child is not null; child = child.NextSibling)
                    {
                        nested |= AppendInline(paragraph, child, headingLevel, bold, italic);
                    }

                    return nested;
                default:
                    return false;
            }
        }

        private bool TryCreateImageParagraph(LinkInline image, out Paragraph paragraph)
        {
            paragraph = new Paragraph();
            var name = ScreenshotName(image.Url);
            if (name is null || !images.TryGetValue(name, out var attachment))
            {
                return false;
            }

            var bytes = ReadImageBytes(attachment);
            if (bytes is null || bytes.Length == 0)
            {
                return false;
            }

            var (cx, cy) = ImageExtent(bytes);
            var contentType = string.IsNullOrWhiteSpace(attachment.MimeType) ? "image/png" : attachment.MimeType;
            var type = contentType.Contains("png", StringComparison.OrdinalIgnoreCase)
                ? ImagePartType.Png
                : contentType.Contains("gif", StringComparison.OrdinalIgnoreCase)
                    ? ImagePartType.Gif
                    : ImagePartType.Jpeg;
            var part = main.AddImagePart(type);
            using (var stream = new MemoryStream(bytes))
            {
                part.FeedData(stream);
            }

            var relationshipId = main.GetIdOfPart(part);
            var id = _drawingId++;
            paragraph = new Paragraph(new Run(CreateDrawing(relationshipId, id, attachment.FileName, cx, cy)));
            return true;
        }

        private static Drawing CreateDrawing(string relationshipId, uint id, string name, long cx, long cy) =>
            new(
                new DW.Inline(
                    new DW.Extent { Cx = cx, Cy = cy },
                    new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                    new DW.DocProperties { Id = id, Name = name },
                    new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(
                        new A.GraphicData(
                            new PIC.Picture(
                                new PIC.NonVisualPictureProperties(
                                    new PIC.NonVisualDrawingProperties { Id = id, Name = name },
                                    new PIC.NonVisualPictureDrawingProperties()),
                                new PIC.BlipFill(
                                    new A.Blip { Embed = relationshipId, CompressionState = A.BlipCompressionValues.Print },
                                    new A.Stretch(new A.FillRectangle())),
                                new PIC.ShapeProperties(
                                    new A.Transform2D(
                                        new A.Offset { X = 0L, Y = 0L },
                                        new A.Extents { Cx = cx, Cy = cy }),
                                    new A.PresetGeometry(new A.AdjustValueList())
                                    {
                                        Preset = A.ShapeTypeValues.Rectangle
                                    })))
                        {
                            Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture"
                        }))
                {
                    DistanceFromTop = 0U,
                    DistanceFromBottom = 0U,
                    DistanceFromLeft = 0U,
                    DistanceFromRight = 0U
                });

        private static (long Cx, long Cy) ImageExtent(byte[] bytes)
        {
        var width = 960;
        var height = 540;
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is { Width: > 0, Height: > 0 })
            {
                width = bitmap.Width;
                height = bitmap.Height;
            }
        }
        catch (Exception)
        {
            // A missing decoder still leaves the image embedded at a default size.
        }

            var cx = width * EmuPerInch / 96;
            var cy = height * EmuPerInch / 96;
            if (cx > MaxWidthEmu && cx > 0)
            {
                cy = cy * MaxWidthEmu / cx;
                cx = MaxWidthEmu;
            }

            return (Math.Max(cx, 1), Math.Max(cy, 1));
        }

        private static Paragraph CodeParagraph(string text, string? marker)
        {
            var paragraph = NewParagraph(headingLevel: 0);
            if (!string.IsNullOrEmpty(marker))
            {
                paragraph.Append(MakeRun(marker, headingLevel: 0, bold: false, italic: false, code: false));
            }

            var lines = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    paragraph.Append(new Run(new Break()));
                }

                paragraph.Append(MakeRun(lines[i], headingLevel: 0, bold: false, italic: false, code: true));
            }

            return paragraph;
        }

        private static Paragraph TextParagraph(string text, string? marker, int headingLevel)
        {
            var paragraph = NewParagraph(headingLevel);
            if (!string.IsNullOrEmpty(marker))
            {
                paragraph.Append(MakeRun(marker, headingLevel, bold: false, italic: false, code: false));
            }

            if (!string.IsNullOrEmpty(text))
            {
                paragraph.Append(MakeRun(text, headingLevel, bold: false, italic: false, code: false));
            }

            return paragraph;
        }

        private static Paragraph NewParagraph(int headingLevel)
        {
            var paragraph = new Paragraph();
            if (headingLevel > 0)
            {
                paragraph.ParagraphProperties = new ParagraphProperties(
                    new SpacingBetweenLines { Before = "160", After = "80" });
            }

            return paragraph;
        }

        private static Run MakeRun(string text, int headingLevel, bool bold, bool italic, bool code)
        {
            var run = new Run();
            var properties = new RunProperties();
            var size = headingLevel switch
            {
                1 => "32",
                2 => "28",
                3 => "24",
                _ => code ? "20" : "22"
            };
            properties.Append(new FontSize { Val = size });
            if (headingLevel is > 0 and <= 3 || bold)
            {
                properties.Append(new Bold());
            }

            if (italic)
            {
                properties.Append(new Italic());
            }

            if (code)
            {
                properties.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
            }

            run.RunProperties = properties;
            run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            return run;
        }

        private static string PlainText(MarkdownObject node)
        {
            var builder = new StringBuilder();
            foreach (var descendant in node.Descendants<LiteralInline>())
            {
                builder.Append(descendant.Content.ToString());
            }

            return builder.ToString().Trim();
        }

        private static string? ScreenshotName(string? url)
        {
            const string prefix = "screenshot:";
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            var name = url[prefix.Length..];
            return name.StartsWith(BrowserFrameFiles.Prefix, StringComparison.Ordinal) ? name : null;
        }
    }
}
