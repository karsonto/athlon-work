using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Athlon.Agent.Core.ComputerUse;

namespace Athlon.Agent.App.Services.ComputerUse;

public static class ComputerUseScreenshotEncoder
{
    public const string MimeType = "image/jpeg";

    public static EncodedScreenshot Encode(BitmapSource source, int captureWidth, int captureHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (captureWidth <= 0 || captureHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captureWidth));
        }

        var (imageWidth, imageHeight) = ComputerUseScreenshotSizing.FitWithin(captureWidth, captureHeight);
        BitmapSource toEncode = source;
        if (imageWidth != captureWidth || imageHeight != captureHeight)
        {
            var scaled = new TransformedBitmap(
                source,
                new ScaleTransform(
                    imageWidth / (double)captureWidth,
                    imageHeight / (double)captureHeight));
            scaled.Freeze();
            toEncode = scaled;
        }

        var encoder = new JpegBitmapEncoder
        {
            QualityLevel = ComputerUseScreenshotSizing.JpegQuality
        };
        encoder.Frames.Add(BitmapFrame.Create(toEncode));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new EncodedScreenshot(stream.ToArray(), captureWidth, captureHeight, imageWidth, imageHeight, MimeType);
    }

    public sealed record EncodedScreenshot(
        byte[] Bytes,
        int CaptureWidth,
        int CaptureHeight,
        int ImageWidth,
        int ImageHeight,
        string MimeType);
}
