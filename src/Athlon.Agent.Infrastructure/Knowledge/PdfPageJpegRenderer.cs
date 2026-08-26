using Athlon.Agent.Core;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Athlon.Agent.Infrastructure.Knowledge;

/// <summary>Renders a single PDF page to a temporary JPEG for knowledge OCR.</summary>
public static class PdfPageJpegRenderer
{
    private const int MaxPreviewDimension = 1400;
    private const int JpegQuality = 82;

    public static async Task<ImageAttachment> RenderPageAsync(
        byte[] pdfBytes,
        string baseName,
        int pageNumber,
        string tempDirectory,
        int renderDpi,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        Directory.CreateDirectory(tempDirectory);
        var outputPath = Path.Combine(tempDirectory, $"{baseName}-p{pageNumber:D4}.jpg");
        var dpi = renderDpi > 0 ? renderDpi : EstimateDpiForPage(pdfBytes, pageNumber);
        Conversion.SaveJpeg(
            outputPath,
            pdfBytes,
            page: pageNumber - 1,
            password: null,
            options: new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true));
        ResizeJpegInPlace(outputPath, MaxPreviewDimension, JpegQuality);

        return new ImageAttachment(
            $"{baseName}-p{pageNumber:D4}.jpg",
            "image/jpeg",
            DataUrl: null,
            LocalPath: outputPath);
    }

    private static int EstimateDpiForPage(byte[] pdfBytes, int pageNumber)
    {
        try
        {
            using var document = PdfDocument.Open(pdfBytes);
            var page = document.GetPage(pageNumber);
            var maxPoint = Math.Max(page.Width, page.Height);
            if (maxPoint <= 0)
            {
                return 120;
            }

            return (int)Math.Clamp(72.0 * MaxPreviewDimension / maxPoint, 72, 144);
        }
        catch
        {
            return 120;
        }
    }

    private static void ResizeJpegInPlace(string path, int maxDimension, int quality)
    {
        using var input = SKBitmap.Decode(path);
        if (input is null)
        {
            return;
        }

        var scale = Math.Min(1.0, maxDimension / (double)Math.Max(input.Width, input.Height));
        SKBitmap bitmap = input;
        SKBitmap? scaled = null;
        try
        {
            if (scale < 0.999)
            {
                var width = Math.Max(1, (int)Math.Round(input.Width * scale));
                var height = Math.Max(1, (int)Math.Round(input.Height * scale));
                scaled = input.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
                if (scaled is not null)
                {
                    bitmap = scaled;
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (data is null)
            {
                return;
            }

            using var output = File.Open(path, FileMode.Create, FileAccess.Write);
            data.SaveTo(output);
        }
        finally
        {
            scaled?.Dispose();
        }
    }
}
