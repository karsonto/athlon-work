namespace Athlon.Agent.Core.ComputerUse;

public static class ComputerUseUiNodeFilter
{
    public static bool ShouldInclude(
        bool isRoot,
        bool isOffscreen,
        double boundsWidth,
        double boundsHeight,
        int? monitorLeft = null,
        int? monitorTop = null,
        int? monitorWidth = null,
        int? monitorHeight = null,
        double? boundsLeft = null,
        double? boundsTop = null)
    {
        if (isRoot)
        {
            return true;
        }

        if (isOffscreen)
        {
            return false;
        }

        if (boundsWidth <= 0 || boundsHeight <= 0)
        {
            return false;
        }

        if (monitorLeft is int left
            && monitorTop is int top
            && monitorWidth is int width
            && monitorHeight is int height
            && boundsLeft is double nodeLeft
            && boundsTop is double nodeTop)
        {
            var nodeRight = nodeLeft + boundsWidth;
            var nodeBottom = nodeTop + boundsHeight;
            var monitorRight = left + width;
            var monitorBottom = top + height;
            var intersects = nodeLeft < monitorRight
                && nodeRight > left
                && nodeTop < monitorBottom
                && nodeBottom > top;
            if (!intersects)
            {
                return false;
            }
        }

        return true;
    }
}
