using Athlon.Agent.Core;
using UglyToad.PdfPig.Content;

namespace Athlon.Agent.Infrastructure.Knowledge;

/// <summary>Exports PdfPig embedded page images to temp files for vision OCR.</summary>
public static class PdfEmbeddedImageExporter
{
    /// <summary>Skip tiny decorative images (logos/icons) that burn OCR quota.</summary>
    public const int MinDimensionSamples = 48;

    public const long MinBoundsArea = 48L * 48L;

    public static bool IsLargeEnough(IPdfImage image)
    {
        var w = image.WidthInSamples;
        var h = image.HeightInSamples;
        if (w >= MinDimensionSamples && h >= MinDimensionSamples)
        {
            return true;
        }

        var bounds = image.Bounds;
        var area = Math.Abs(bounds.Width * bounds.Height);
        return area >= MinBoundsArea;
    }

    /// <summary>
    /// Writes a decodable image to <paramref name="tempDirectory"/>.
    /// Returns null when bytes cannot be obtained.
    /// </summary>
    public static ImageAttachment? TryExport(
        IPdfImage image,
        string baseName,
        int pageNumber,
        int imageIndex,
        string tempDirectory)
    {
        Directory.CreateDirectory(tempDirectory);

        if (!TryGetImageBytes(image, out var bytes, out var extension, out var mimeType)
            || bytes is null
            || bytes.Length == 0)
        {
            return null;
        }

        var fileName = $"{baseName}-p{pageNumber:D4}-i{imageIndex:D2}{extension}";
        var path = Path.Combine(tempDirectory, fileName);
        File.WriteAllBytes(path, bytes);
        return new ImageAttachment(fileName, mimeType, LocalPath: path);
    }

    private static bool TryGetImageBytes(
        IPdfImage image,
        out byte[]? bytes,
        out string extension,
        out string mimeType)
    {
        bytes = null;
        extension = ".png";
        mimeType = "image/png";

        try
        {
            if (image.TryGetPng(out var png) && png is { Length: > 0 })
            {
                bytes = png;
                return true;
            }
        }
        catch
        {
            // Fall through to raw bytes.
        }

        try
        {
            if (image.TryGetBytes(out var decoded) && decoded is { Count: > 0 })
            {
                bytes = decoded as byte[] ?? decoded.ToArray();
                // Raw PDF samples are not always a file format; prefer treating as opaque PNG-like dump fails.
                // Many DCT streams are JPEG in RawBytes when TryGetPng fails.
            }
        }
        catch
        {
            // Fall through.
        }

        var raw = image.RawBytes;
        if (raw is { Count: > 0 })
        {
            var rawArray = raw as byte[] ?? raw.ToArray();
            if (LooksLikeJpeg(rawArray))
            {
                bytes = rawArray;
                extension = ".jpg";
                mimeType = "image/jpeg";
                return true;
            }

            if (LooksLikePng(rawArray))
            {
                bytes = rawArray;
                extension = ".png";
                mimeType = "image/png";
                return true;
            }

            // Last resort: use raw when TryGetBytes already filled bytes, else raw.
            bytes ??= rawArray;
            if (bytes is { Length: > 0 } && LooksLikeJpeg(bytes))
            {
                extension = ".jpg";
                mimeType = "image/jpeg";
                return true;
            }
        }

        // Undecodable sample buffers (raw bitmap) are not useful for vision APIs.
        if (bytes is { Length: > 0 } && (LooksLikeJpeg(bytes) || LooksLikePng(bytes)))
        {
            return true;
        }

        bytes = null;
        return false;
    }

    private static bool LooksLikeJpeg(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static bool LooksLikePng(byte[] bytes) =>
        bytes.Length >= 8
        && bytes[0] == 0x89
        && bytes[1] == (byte)'P'
        && bytes[2] == (byte)'N'
        && bytes[3] == (byte)'G';
}
