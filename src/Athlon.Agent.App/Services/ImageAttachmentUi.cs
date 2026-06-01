using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Athlon.Agent.App.Services;

public static class ImageAttachmentUi
{
    public static ImageSource? TryCreateThumbnail(string? dataUrl, int decodePixelWidth = 96)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)
            || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(dataUrl, UriKind.Absolute);
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
