namespace Athlon.Agent.Core.ComputerUse;

public static class ComputerUseDragPath
{
    public const int DefaultSteps = 16;

    public static IReadOnlyList<(int X, int Y)> Build(
        int startX,
        int startY,
        int endX,
        int endY,
        int steps = DefaultSteps)
    {
        if (steps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(steps));
        }

        if (startX == endX && startY == endY)
        {
            return [(endX, endY)];
        }

        var points = new (int X, int Y)[steps];
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            points[i - 1] = (
                startX + (int)Math.Round((endX - startX) * t),
                startY + (int)Math.Round((endY - startY) * t));
        }

        // Ensure the final sample lands exactly on the requested end point.
        points[^1] = (endX, endY);
        return points;
    }
}
