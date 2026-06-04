using System.IO;
using Athlon.Agent.Core;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Athlon.Agent.App.Services;

public static class ImageAttachmentUi
{
    public static ImageSource? TryCreateThumbnail(ImageAttachment attachment, int decodePixelWidth = 96) =>
        TryCreateThumbnailFromPath(attachment.LocalPath, decodePixelWidth)
        ?? TryCreateThumbnail(attachment.DataUrl, decodePixelWidth);

    public static ImageSource? TryCreateThumbnail(string? dataUrl, int decodePixelWidth = 96)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)
            || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return LoadBitmap(new Uri(dataUrl, UriKind.Absolute), decodePixelWidth);
    }

    private static ImageSource? TryCreateThumbnailFromPath(string? localPath, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            return null;
        }

        return LoadBitmap(new Uri(localPath, UriKind.Absolute), decodePixelWidth);
    }

    private static ImageSource? LoadBitmap(Uri uri, int decodePixelWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = uri;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
