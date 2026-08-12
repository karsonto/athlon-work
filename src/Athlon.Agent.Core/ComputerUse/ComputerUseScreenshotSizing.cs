namespace Athlon.Agent.Core.ComputerUse;

public static class ComputerUseScreenshotSizing
{
    public const int MaxLongestEdge = 1600;
    public const int JpegQuality = 80;

    public static (int Width, int Height) FitWithin(int width, int height, int maxLongestEdge = MaxLongestEdge)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        }

        if (maxLongestEdge <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLongestEdge));
        }

        var longest = Math.Max(width, height);
        if (longest <= maxLongestEdge)
        {
            return (width, height);
        }

        var scale = maxLongestEdge / (double)longest;
        var fittedWidth = Math.Max(1, (int)Math.Round(width * scale));
        var fittedHeight = Math.Max(1, (int)Math.Round(height * scale));
        return (fittedWidth, fittedHeight);
    }
}
