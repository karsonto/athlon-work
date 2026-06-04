namespace Athlon.Agent.Core;

public static class ImageAttachmentDataUrlResolver
{
    public static string? ResolveDataUrl(ImageAttachment image)
    {
        if (!string.IsNullOrWhiteSpace(image.DataUrl))
        {
            return image.DataUrl;
        }

        if (string.IsNullOrWhiteSpace(image.LocalPath) || !File.Exists(image.LocalPath))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(image.LocalPath);
        var mime = string.IsNullOrWhiteSpace(image.MimeType) ? "application/octet-stream" : image.MimeType;
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }
}
