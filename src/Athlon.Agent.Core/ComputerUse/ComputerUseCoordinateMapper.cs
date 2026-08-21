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

    public static (int X, int Y) PhysicalToImage(
        int physicalX,
        int physicalY,
        int monitorLeft,
        int monitorTop,
        int captureWidth,
        int captureHeight,
        int imageWidth,
        int imageHeight)
    {
        var (x, y, _, _) = PhysicalRectToImage(
            physicalX,
            physicalY,
            physicalWidth: 0,
            physicalHeight: 0,
            monitorLeft,
            monitorTop,
            captureWidth,
            captureHeight,
            imageWidth,
            imageHeight);
        return (x, y);
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

    /// <summary>
    /// True when image coordinates fall inside the screenshot pixel grid (half-open).
    /// Values outside usually mean the caller passed physical desktop pixels by mistake.
    /// </summary>
    public static bool IsImagePointInFrame(int imageX, int imageY, int imageWidth, int imageHeight) =>
        imageWidth > 0
        && imageHeight > 0
        && imageX >= 0
        && imageY >= 0
        && imageX < imageWidth
        && imageY < imageHeight;

    private static int ScaleAndClamp(long value, int sourceSize, int targetSize)
    {
        var scaled = (int)Math.Round(value * targetSize / (double)sourceSize);
        return Math.Clamp(scaled, 0, targetSize);
    }
}

/// <summary>
/// Resolves whether a pointer action should use screenshot pixels or a UIA element.
/// Screenshot image_x/image_y always wins when present so vision targeting is not
/// silently overridden by a coarse element_id from a shallow post-action tree.
/// </summary>
public static class ComputerUsePointerTargetPolicy
{
    public static bool PreferImagePoint(bool hasElementId, bool hasImagePoint) =>
        hasImagePoint;

    public static bool PreferElementClickablePoint(bool hasElementId, bool hasImagePoint) =>
        hasElementId && !hasImagePoint;
}
