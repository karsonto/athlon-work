namespace Athlon.Agent.Core.ComputerUse;

public static class ComputerUseCoordinateMapper
{
    public static (int X, int Y) ImageToPhysical(
        int imageX,
        int imageY,
        int monitorLeft,
        int monitorTop,
        int captureWidth,
        int captureHeight,
        int imageWidth,
        int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || captureWidth <= 0 || captureHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageWidth),
                "Capture and image dimensions must be positive.");
        }

        var physicalX = monitorLeft + (int)Math.Round(imageX * (double)captureWidth / imageWidth);
        var physicalY = monitorTop + (int)Math.Round(imageY * (double)captureHeight / imageHeight);
        return (physicalX, physicalY);
    }

    public static (int X, int Y, int Width, int Height) PhysicalRectToImage(
        int physicalX,
        int physicalY,
        int physicalWidth,
        int physicalHeight,
        int monitorLeft,
        int monitorTop,
        int captureWidth,
        int captureHeight,
        int imageWidth,
        int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || captureWidth <= 0 || captureHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageWidth),
                "Capture and image dimensions must be positive.");
        }

        var left = ScaleAndClamp(
            physicalX - monitorLeft,
            captureWidth,
            imageWidth);
        var top = ScaleAndClamp(
            physicalY - monitorTop,
            captureHeight,
            imageHeight);
        var right = ScaleAndClamp(
            (long)physicalX + Math.Max(0, physicalWidth) - monitorLeft,
            captureWidth,
            imageWidth);
        var bottom = ScaleAndClamp(
            (long)physicalY + Math.Max(0, physicalHeight) - monitorTop,
            captureHeight,
            imageHeight);

        return (
            left,
            top,
            Math.Max(0, right - left),
            Math.Max(0, bottom - top));
    }

    private static int ScaleAndClamp(long value, int sourceSize, int targetSize)
    {
        var scaled = (int)Math.Round(value * targetSize / (double)sourceSize);
        return Math.Clamp(scaled, 0, targetSize);
    }
}
