using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed class ClipboardImageAttachmentReader(IImageAttachmentReader imageAttachmentReader)
{
    public async Task<IReadOnlyList<ImageAttachment>> TryReadImagesAsync(
        CancellationToken cancellationToken = default)
    {
        if (Clipboard.ContainsFileDropList())
        {
            var paths = Clipboard.GetFileDropList()
                .Cast<string>()
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
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

        if (!Clipboard.ContainsImage())
        {
            return Array.Empty<ImageAttachment>();
        }

        var bitmap = Clipboard.GetImage();
        if (bitmap is null)
        {
            return Array.Empty<ImageAttachment>();
        }

        var attachment = CreateFromBitmap(bitmap);
        return attachment is null ? Array.Empty<ImageAttachment>() : [attachment];
    }

    public IReadOnlyList<ImageAttachment> TryReadImages() =>
        TryReadImagesAsync().GetAwaiter().GetResult();

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
}
