using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed class ClipboardImageAttachmentReader(IImageAttachmentReader imageAttachmentReader)
{
    private static readonly string[] AdditionalImageFormats =
    [
        "PNG",
        "image/png",
        "JFIF",
        "image/jpeg",
        "WEBP",
        "image/webp",
    ];

    public bool HasPotentialPasteAttachments()
    {
        if (Clipboard.ContainsFileDropList() || Clipboard.ContainsImage())
        {
            return true;
        }

        var data = Clipboard.GetDataObject();
        return data is not null && HasImageFormats(data);
    }

    /// <summary>Backward-compatible alias. </summary>
    public bool HasPotentialImages() => HasPotentialPasteAttachments();

    public string[] GetClipboardFilePaths()
    {
        if (!Clipboard.ContainsFileDropList())
        {
            return [];
        }

        return Clipboard.GetFileDropList()
            .Cast<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
    }

    public async Task<IReadOnlyList<ImageAttachment>> TryReadImagesAsync(
        CancellationToken cancellationToken = default)
    {
        if (Clipboard.ContainsFileDropList())
        {
            var paths = GetClipboardFilePaths();
            if (paths.Length > 0)
            {
                var images = await imageAttachmentReader.ReadImagesAsync(paths, cancellationToken)
                    .ConfigureAwait(true);
                if (images.Count > 0)
                {
                    return images;
                }
            }
        }

        var data = Clipboard.GetDataObject();
        if (data is null)
        {
            return Array.Empty<ImageAttachment>();
        }

        var bitmap = TryReadBitmapFromDataObject(data);
        if (bitmap is null)
        {
            return Array.Empty<ImageAttachment>();
        }

        var attachment = CreateFromBitmap(bitmap);
        return attachment is null ? Array.Empty<ImageAttachment>() : [attachment];
    }

    internal static bool HasImageFormats(IDataObject data) =>
        data.GetDataPresent(DataFormats.Bitmap, autoConvert: true) ||
        AdditionalImageFormats.Any(format => data.GetDataPresent(format));

    internal static BitmapSource? TryReadBitmapFromDataObject(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.Bitmap, autoConvert: true))
        {
            try
            {
                var image = Clipboard.GetImage();
                if (image is not null)
                {
                    return image;
                }
            }
            catch
            {
                // Clipboard may be locked by another process; fall back to raw formats.
            }

            if (data.GetData(DataFormats.Bitmap, autoConvert: true) is BitmapSource bitmap)
            {
                return bitmap;
            }
        }

        foreach (var format in AdditionalImageFormats)
        {
            if (!data.GetDataPresent(format))
            {
                continue;
            }

            var bytes = ExtractBytes(data.GetData(format));
            if (bytes is null)
            {
                continue;
            }

            var decoded = DecodeImageBytes(bytes);
            if (decoded is not null)
            {
                return decoded;
            }
        }

        return null;
    }

    internal static byte[]? ExtractBytes(object? raw) =>
        raw switch
        {
            byte[] buffer when buffer.Length > 0 => buffer,
            MemoryStream stream => stream.ToArray(),
            Stream stream => ReadAllBytes(stream),
            _ => null
        };

    internal static BitmapSource? DecodeImageBytes(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var frame = decoder.Frames[0];
            if (frame.CanFreeze)
            {
                frame.Freeze();
            }

            return frame;
        }
        catch
        {
            return null;
        }
    }

    internal static ImageAttachment? CreateFromBitmap(BitmapSource bitmap, string? fileName = null)
    {
        try
        {
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            var base64 = Convert.ToBase64String(stream.ToArray());
            var name = string.IsNullOrWhiteSpace(fileName)
                ? $"clipboard-{Guid.NewGuid():N}.png"
                : fileName;
            return new ImageAttachment(name, "image/png", $"data:image/png;base64,{base64}");
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReadAllBytes(Stream stream)
    {
        try
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
