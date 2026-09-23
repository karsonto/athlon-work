using System.Text;

namespace Athlon.Agent.Core.ComputerUse;

/// <summary>
/// Writes one UI Automation node per line as compact JSON. The previous serializer indented every
/// property of every node, which inflated the observed tree roughly 15x in whitespace alone; this
/// keeps one node per line so the tree stays diffable and readable while the payload stays small.
/// Pure and primitive-typed so it is unit-testable without a desktop.
/// </summary>
public static class ComputerUseUiTreeWriter
{
    public static void WriteNode(
        StringBuilder writer,
        string elementId,
        string? parentId,
        int depth,
        string? name,
        string controlType,
        string? automationId,
        bool enabled,
        bool offscreen,
        bool focusable,
        int? boundsLeft,
        int? boundsTop,
        int? boundsWidth,
        int? boundsHeight,
        int? captureLeft,
        int? captureTop,
        int? captureWidth,
        int? captureHeight,
        int? imageWidth,
        int? imageHeight)
    {
        writer.Append('{');
        WriteString(writer, "element_id", elementId, isFirst: true);
        WriteString(writer, "parent_id", parentId);
        WriteNumber(writer, "depth", depth);
        WriteString(writer, "name", name);
        WriteString(writer, "control_type", controlType);
        WriteString(writer, "automation_id", automationId);
        WriteBool(writer, "enabled", enabled);
        WriteBool(writer, "offscreen", offscreen);
        WriteBool(writer, "focusable", focusable);
        WriteBounds(writer, boundsLeft, boundsTop, boundsWidth, boundsHeight);
        WriteImageBounds(
            writer,
            boundsLeft,
            boundsTop,
            boundsWidth,
            boundsHeight,
            captureLeft,
            captureTop,
            captureWidth,
            captureHeight,
            imageWidth,
            imageHeight);
        writer.Append('}');
    }

    private static void WriteBounds(
        StringBuilder writer,
        int? left,
        int? top,
        int? width,
        int? height)
    {
        if (left is not int x || top is not int y || width is not int w || height is not int h)
        {
            return;
        }

        writer.Append(",\"bounds\":{");
        writer.Append("\"x\":").Append(x);
        writer.Append(",\"y\":").Append(y);
        writer.Append(",\"width\":").Append(w);
        writer.Append(",\"height\":").Append(h);
        writer.Append('}');
    }

    private static void WriteImageBounds(
        StringBuilder writer,
        int? boundsLeft,
        int? boundsTop,
        int? boundsWidth,
        int? boundsHeight,
        int? captureLeft,
        int? captureTop,
        int? captureWidth,
        int? captureHeight,
        int? imageWidth,
        int? imageHeight)
    {
        if (boundsLeft is not int left
            || boundsTop is not int top
            || boundsWidth is not int width
            || boundsHeight is not int height
            || captureLeft is not int captureX
            || captureTop is not int captureY
            || captureWidth is not int captureW
            || captureHeight is not int captureH
            || imageWidth is not > 0
            || imageHeight is not > 0)
        {
            return;
        }

        var mapped = ComputerUseCoordinateMapper.PhysicalRectToImage(
            left,
            top,
            width,
            height,
            captureX,
            captureY,
            captureW,
            captureH,
            imageWidth.Value,
            imageHeight.Value);
        writer.Append(",\"image_bounds\":{");
        writer.Append("\"x\":").Append(mapped.X);
        writer.Append(",\"y\":").Append(mapped.Y);
        writer.Append(",\"width\":").Append(mapped.Width);
        writer.Append(",\"height\":").Append(mapped.Height);
        writer.Append('}');
    }

    private static void WriteString(StringBuilder writer, string name, string? value, bool isFirst = false)
    {
        if (value is null)
        {
            return;
        }

        WriteSeparator(writer, isFirst);
        writer.Append('"').Append(name).Append("\":");
        WriteEscaped(writer, value);
    }

    private static void WriteNumber(StringBuilder writer, string name, int value)
    {
        WriteSeparator(writer);
        writer.Append('"').Append(name).Append("\":").Append(value);
    }

    private static void WriteBool(StringBuilder writer, string name, bool value)
    {
        WriteSeparator(writer);
        writer.Append('"').Append(name).Append("\":").Append(value ? "true" : "false");
    }

    private static void WriteSeparator(StringBuilder writer, bool isFirst = false)
    {
        if (!isFirst)
        {
            writer.Append(',');
        }
    }

    private static void WriteEscaped(StringBuilder writer, string value)
    {
        writer.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    writer.Append("\\\"");
                    break;
                case '\\':
                    writer.Append("\\\\");
                    break;
                case '\n':
                    writer.Append("\\n");
                    break;
                case '\r':
                    writer.Append("\\r");
                    break;
                case '\t':
                    writer.Append("\\t");
                    break;
                default:
                    if (ch < ' ')
                    {
                        writer.Append("\\u").Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        writer.Append(ch);
                    }

                    break;
            }
        }

        writer.Append('"');
    }
}
