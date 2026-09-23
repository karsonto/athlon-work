using System.Text;
using System.Text.Json;
using Athlon.Agent.Core.ComputerUse;

namespace Athlon.Agent.Tests;

/// <summary>
/// The observation envelope used to indent every property of every UI node, roughly 15x the
/// whitespace of the node data itself. These tests lock in the compact one-node-per-line shape.
/// </summary>
public sealed class ComputerUseUiTreePayloadTests
{
    [Fact]
    public void WriteNode_ProducesSingleLinePerNode()
    {
        var writer = new StringBuilder();
        ComputerUseUiTreeWriter.WriteNode(
            writer,
            elementId: "ui_1",
            parentId: null,
            depth: 0,
            name: "Window",
            controlType: "Window",
            automationId: "main",
            enabled: true,
            offscreen: false,
            focusable: true,
            boundsLeft: 0,
            boundsTop: 0,
            boundsWidth: 800,
            boundsHeight: 600,
            captureLeft: 0,
            captureTop: 0,
            captureWidth: 800,
            captureHeight: 600,
            imageWidth: 800,
            imageHeight: 600);

        var json = writer.ToString();
        Assert.DoesNotContain('\n', json);
        Assert.StartsWith("{\"element_id\":\"ui_1\",", json, StringComparison.Ordinal);
        Assert.EndsWith("}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteNode_IsValidJsonWithExpectedFields()
    {
        var writer = new StringBuilder();
        ComputerUseUiTreeWriter.WriteNode(
            writer,
            "ui_5",
            "ui_1",
            2,
            "OK",
            "Button",
            "okButton",
            enabled: true,
            offscreen: false,
            focusable: true,
            boundsLeft: 10,
            boundsTop: 20,
            boundsWidth: 100,
            boundsHeight: 40,
            captureLeft: 0,
            captureTop: 0,
            captureWidth: 1000,
            captureHeight: 1000,
            imageWidth: 500,
            imageHeight: 500);

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.Equal("ui_5", root.GetProperty("element_id").GetString());
        Assert.Equal("ui_1", root.GetProperty("parent_id").GetString());
        Assert.Equal(2, root.GetProperty("depth").GetInt32());
        Assert.Equal("Button", root.GetProperty("control_type").GetString());
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(10, root.GetProperty("bounds").GetProperty("x").GetInt32());

        // 1000px capture at 500px image means halves.
        var imageBounds = root.GetProperty("image_bounds");
        Assert.Equal(5, imageBounds.GetProperty("x").GetInt32());
        Assert.Equal(10, imageBounds.GetProperty("y").GetInt32());
        Assert.Equal(50, imageBounds.GetProperty("width").GetInt32());
        Assert.Equal(20, imageBounds.GetProperty("height").GetInt32());
    }

    [Fact]
    public void WriteNode_OmitsMissingOptionalFields()
    {
        var writer = new StringBuilder();
        ComputerUseUiTreeWriter.WriteNode(
            writer,
            "ui_1",
            null,
            0,
            name: null,
            "Unknown",
            automationId: null,
            enabled: false,
            offscreen: true,
            focusable: false,
            boundsLeft: null,
            boundsTop: null,
            boundsWidth: null,
            boundsHeight: null,
            captureLeft: null,
            captureTop: null,
            captureWidth: null,
            captureHeight: null,
            imageWidth: null,
            imageHeight: null);

        var json = writer.ToString();
        Assert.DoesNotContain("parent_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("name", json, StringComparison.Ordinal);
        Assert.DoesNotContain("automation_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bounds", json, StringComparison.Ordinal);
        Assert.DoesNotContain("image_bounds", json, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("ui_1", document.RootElement.GetProperty("element_id").GetString());
    }

    [Fact]
    public void WriteNode_OmitsImageBoundsWhenCaptureGeometryIncomplete()
    {
        var writer = new StringBuilder();
        ComputerUseUiTreeWriter.WriteNode(
            writer,
            "ui_1",
            null,
            0,
            "X",
            "Button",
            null,
            true,
            false,
            true,
            boundsLeft: 1,
            boundsTop: 2,
            boundsWidth: 3,
            boundsHeight: 4,
            captureLeft: null,
            captureTop: null,
            captureWidth: null,
            captureHeight: null,
            imageWidth: 100,
            imageHeight: 100);

        Assert.DoesNotContain("image_bounds", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteNode_EscapesControlCharactersSoPayloadStaysSingleLine()
    {
        var writer = new StringBuilder();
        ComputerUseUiTreeWriter.WriteNode(
            writer,
            "ui_1",
            null,
            0,
            "line1\nline2\ttab\"quote\\slash",
            "Text",
            null,
            true,
            false,
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        var json = writer.ToString();
        Assert.DoesNotContain('\n', json);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            "line1\nline2\ttab\"quote\\slash",
            document.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void CountNodes_CountsObjectsInArray()
    {
        const string json = """
            [
            {"element_id":"ui_1"},
            {"element_id":"ui_2","bounds":{"x":1,"y":2}},
            {"element_id":"ui_3"}
            ]
            """;

        Assert.Equal(3, ComputerUseUiTreeMetrics.CountNodes(json));
    }

    [Fact]
    public void CountNodes_HandlesEmptyAndNestedObjects()
    {
        Assert.Equal(0, ComputerUseUiTreeMetrics.CountNodes("[]"));
        Assert.Equal(0, ComputerUseUiTreeMetrics.CountNodes(null));
        Assert.Equal(0, ComputerUseUiTreeMetrics.CountNodes(string.Empty));

        // Braces inside string values must not be counted as node boundaries.
        const string withBracesInString = """[{"name":"brace } here"}]""";
        Assert.Equal(1, ComputerUseUiTreeMetrics.CountNodes(withBracesInString));
    }

    [Fact]
    public void CountNodes_IgnoresEscapedQuotes()
    {
        const string json = """[{"name":"he said \"hi\""},{"name":"second"}]""";

        Assert.Equal(2, ComputerUseUiTreeMetrics.CountNodes(json));
    }

    [Fact]
    public void ObservationLimits_ExposeNarrowedDefaults()
    {
        // Phase 1.3 narrowed the tree defaults; the tool schema and host clamp must agree.
        Assert.Equal(3, ComputerUseSettingsDefaults.DefaultMaxTreeDepth);
        Assert.Equal(40, ComputerUseSettingsDefaults.DefaultMaxNodes);
        Assert.True(ComputerUseSettingsDefaults.DefaultMaxTreeDepth >= ComputerUseObservationLimits.MinTreeDepth);
        Assert.True(ComputerUseSettingsDefaults.DefaultMaxTreeDepth <= ComputerUseObservationLimits.MaxTreeDepth);
        Assert.True(ComputerUseSettingsDefaults.DefaultMaxNodes >= ComputerUseObservationLimits.MinNodes);
        Assert.True(ComputerUseSettingsDefaults.DefaultMaxNodes <= ComputerUseObservationLimits.MaxNodes);
    }
}
