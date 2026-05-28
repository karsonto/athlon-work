using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class ImageAttachmentReader : IImageAttachmentReader
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".gif"
    };

    public async Task<IReadOnlyList<ImageAttachment>> ReadImagesAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ImageAttachment>();
        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                continue;
            }

            var extension = Path.GetExtension(filePath);
            if (!SupportedExtensions.Contains(extension))
            {
                continue;
            }

            var mime = extension.ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(mime))
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var base64 = Convert.ToBase64String(bytes);
            result.Add(new ImageAttachment(
                Path.GetFileName(filePath),
                mime,
                $"data:{mime};base64,{base64}"));
        }

        return result;
    }
}
