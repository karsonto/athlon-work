using System.IO;
using Athlon.Agent.Core;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Athlon.Agent.App.Services;

public static class ImageAttachmentUi
{
    public static ImageSource? TryCreateThumbnail(ImageAttachment attachment, int decodePixelWidth = 96) =>
        TryCreateThumbnailFromPath(attachment.LocalPath, decodePixelWidth)
        ?? TryCreateThumbnailFromDataUrl(attachment.DataUrl, decodePixelWidth);

    public static ImageSource? TryCreateThumbnailFromDataUrl(string? dataUrl, int decodePixelWidth = 96)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)
            || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var commaIndex = dataUrl.IndexOf(',');
            if (commaIndex < 0 || commaIndex >= dataUrl.Length - 1)
            {
                return null;
            }

            var payload = dataUrl[(commaIndex + 1)..];
            var bytes = Convert.FromBase64String(payload);
            using var stream = new MemoryStream(bytes);
            return LoadBitmap(stream, decodePixelWidth);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryCreateThumbnailFromPath(string? localPath, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            return null;
        }

        try
        {
            return LoadBitmap(new Uri(localPath, UriKind.Absolute), decodePixelWidth);
        }
        catch
        {
            return null;
        }
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

    private static ImageSource? LoadBitmap(Stream stream, int decodePixelWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
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
