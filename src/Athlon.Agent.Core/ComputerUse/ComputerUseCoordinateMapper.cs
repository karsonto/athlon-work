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
}
