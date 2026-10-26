using System.IO;
using System.Windows.Media.Imaging;
using Athlon.Agent.App.Services.ComputerUse;
using Athlon.Agent.Core.ComputerUse;

namespace Athlon.Agent.App.Services.Browser;

/// <summary>
/// Shrinks a CDP PNG before it is sent to the model. Same longest-edge and JPEG budget as
/// Computer Use screenshots, so a full-page capture does not become a multi-megabyte RGBA PNG.
/// </summary>
public static class BrowserScreenshotEncoder
{
    public static EncodedBrowserFrame Shrink(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
        {
            return new EncodedBrowserFrame(pngBytes, "image/png");
        }

        try
        {
            using var input = new MemoryStream(pngBytes, writable: false);
            var decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return new EncodedBrowserFrame(pngBytes, "image/png");
            }

            var frame = decoder.Frames[0];
            if (frame.CanFreeze)
            {
                frame.Freeze();
            }

            var encoded = ComputerUseScreenshotEncoder.Encode(frame, frame.PixelWidth, frame.PixelHeight);
            return new EncodedBrowserFrame(encoded.Bytes, encoded.MimeType);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
        {
            return new EncodedBrowserFrame(pngBytes, "image/png");
        }
    }

    /// <summary>CDP clip scale that keeps the longer CSS edge within the model screenshot budget.</summary>
    public static double FullPageScale(double widthCssPx, double heightCssPx)
    {
        var longest = Math.Max(widthCssPx, heightCssPx);
        if (longest <= ComputerUseScreenshotSizing.MaxLongestEdge || longest <= 0)
        {
            return 1;
        }

        return ComputerUseScreenshotSizing.MaxLongestEdge / longest;
    }

    public readonly record struct EncodedBrowserFrame(byte[] Bytes, string MimeType);
}
