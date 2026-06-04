using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public interface IImageAttachmentStore
{
    ImageAttachment SaveFromFile(string sessionId, string sourcePath);
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

        var directory = Path.Combine(_paths.SessionsPath, sessionId, "attachments");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
        File.Copy(sourcePath, destination, overwrite: true);

        return new ImageAttachment(
            Path.GetFileName(sourcePath),
            mime,
            DataUrl: null,
            LocalPath: destination);
    }

    private static string MimeFromExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => string.Empty
        };
}
