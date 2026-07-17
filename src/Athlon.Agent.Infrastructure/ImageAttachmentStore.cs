using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public interface IImageAttachmentStore
{
    ImageAttachment SaveFromFile(string sessionId, string sourcePath);

    ImageAttachment SaveBytes(string sessionId, string fileName, string mimeType, byte[] bytes);
}

public sealed class ImageAttachmentStore : IImageAttachmentStore
{
    private readonly IAppPathProvider _paths;

    public ImageAttachmentStore(IAppPathProvider paths) => _paths = paths;

    public ImageAttachment SaveFromFile(string sessionId, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Image source file was not found.", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath);
        var mime = MimeFromExtension(extension);
        if (string.IsNullOrWhiteSpace(mime))
        {
            throw new InvalidOperationException($"Unsupported image type: {extension}");
        }

        var directory = GetAttachmentsDirectory(sessionId);
        var destination = Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
        File.Copy(sourcePath, destination, overwrite: true);

        return new ImageAttachment(
            Path.GetFileName(sourcePath),
            mime,
            DataUrl: null,
            LocalPath: destination);
    }

    public ImageAttachment SaveBytes(string sessionId, string fileName, string mimeType, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        if (bytes is null || bytes.Length == 0)
        {
            throw new ArgumentException("Image bytes are required.", nameof(bytes));
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ExtensionFromMime(mimeType);
            fileName = string.IsNullOrWhiteSpace(fileName)
                ? $"attachment{extension}"
                : fileName + extension;
        }

        var mime = string.IsNullOrWhiteSpace(mimeType) ? MimeFromExtension(extension) : mimeType;
        if (string.IsNullOrWhiteSpace(mime))
        {
            throw new InvalidOperationException($"Unsupported image type: {extension}");
        }

        var directory = GetAttachmentsDirectory(sessionId);
        var destination = Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(destination, bytes);

        return new ImageAttachment(
            Path.GetFileName(fileName),
            mime,
            DataUrl: null,
            LocalPath: destination);
    }

    private string GetAttachmentsDirectory(string sessionId)
    {
        var directory = Path.Combine(_paths.SessionsPath, sessionId, "attachments");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string MimeFromExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => string.Empty
        };

    private static string ExtensionFromMime(string mimeType) =>
        mimeType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            _ => ".bin"
        };
}
