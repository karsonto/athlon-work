using System.Buffers;
using System.Text;
using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.ComputerUse;

namespace Athlon.Agent.Infrastructure.ComputerUse;

public sealed class ComputerObserveTool(
    IComputerUseAutomationHost host,
    AppSettings settings) : IAgentTool, IComputerUseTool
{
    private ComputerUseSettings ComputerUse => settings.ComputerUse;

    public ToolDefinition Definition { get; } = new(
        "computer_observe",
        "Capture the desktop: screenshot, frame id, foreground window, cursor, display geometry, and an optional bounded UI Automation tree. Nodes carry physical bounds and image_bounds relative to the screenshot. Defaults to the monitor under the cursor; pass monitor_index or window_title to target a display or app without moving the pointer. Observe before interacting.",
        ToolSchema.Object()
            .Boolean("include_ui_tree", "Include the bounded UI Automation tree.", defaultValue: true)
            .Integer(
                "max_tree_depth",
                "Maximum UI tree depth.",
                defaultValue: ComputerUseSettingsDefaults.DefaultMaxTreeDepth,
                minimum: ComputerUseObservationLimits.MinTreeDepth,
                maximum: ComputerUseObservationLimits.MaxTreeDepth)
            .Integer(
                "max_nodes",
                "Maximum UI nodes.",
                defaultValue: ComputerUseSettingsDefaults.DefaultMaxNodes,
                minimum: ComputerUseObservationLimits.MinNodes,
                maximum: ComputerUseObservationLimits.MaxNodes)
            .Integer("monitor_index", "Zero-based display index. Defaults to the display under the cursor.")
            .String("window_title", "Capture the visible window whose title contains this text instead of the cursor display.")
            .String("window_process_name", "Optional process-name filter when window_title matches several windows.")
            .Build(),
        Source: "computer-use");

    public Task<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default) =>
        ComputerUseToolHelper.InvokeAsync(async ct =>
        {
            var observation = await host.ObserveAsync(
                new ComputerUseObserveRequest(
                    invocation.Arguments.GetBoolean("include_ui_tree", true),
                    invocation.Arguments.GetInt32("max_tree_depth", ComputerUse.DefaultMaxTreeDepth),
                    invocation.Arguments.GetInt32("max_nodes", ComputerUse.DefaultMaxNodes),
                    GetNullableInt(invocation, "monitor_index"),
                    invocation.Arguments.GetString("window_title"),
                    invocation.Arguments.GetString("window_process_name")),
                ct).ConfigureAwait(false);
            return ComputerUseToolHelper.FromObservation("Desktop observed", observation);
        }, cancellationToken);

    private static int? GetNullableInt(ToolInvocation invocation, string name) =>
        invocation.Arguments.TryGetInt32(name, out var value) ? value : null;
}

public sealed class ComputerInteractTool(IComputerUseAutomationHost host) : IAgentTool, IComputerUseTool
{
    public ToolDefinition Definition { get; } = new(
        "computer_interact",
        "Perform exactly one desktop action against a fresh frame. Prefer element_id: the host acts through the control itself and never moves the pointer. Use image_x/image_y for canvas or self-drawn targets; right_click and drag require them. Returns a post-action screenshot and a new frame id; call computer_observe for a fresh UI tree.",
        ToolSchema.Object()
            .String("frame_id", "Frame id from the latest computer_observe.", required: true, minLength: 1)
            .String(
                "action",
                "One action: click, double_click, right_click, type_text, key, hotkey, scroll, or drag.",
                required: true,
                enumValues: ["click", "double_click", "right_click", "type_text", "key", "hotkey", "scroll", "drag"])
            .String("element_id", "UI Automation element id from the observed tree. Preferred target; also focuses type_text/key/hotkey.")
            .Integer("image_x", "Screenshot pixel X from image_bounds. Required for right_click and drag.")
            .Integer("image_y", "Screenshot pixel Y from image_bounds.")
            .Integer("end_image_x", "Drag destination X in screenshot pixels.")
            .Integer("end_image_y", "Drag destination Y in screenshot pixels.")
            .Integer("x", "Physical desktop X fallback. Never screenshot pixels.")
            .Integer("y", "Physical desktop Y fallback. Never screenshot pixels.")
            .Integer("end_x", "Drag destination physical desktop X.")
            .Integer("end_y", "Drag destination physical desktop Y.")
            .String("text", "Text for type_text.")
            .String("key", "Key or hotkey expression, for example ENTER or CTRL+S.")
            .Integer("scroll_delta", "Wheel delta; positive scrolls up, negative scrolls down.")
            .Build(),
        RequiresApproval: true,
        Source: "computer-use",
        InvocationPolicy: ToolInvocationPolicy.Ask);

    public Task<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default) =>
        ComputerUseToolHelper.InvokeAsync(async ct =>
        {
            var request = new ComputerUseInteractRequest(
                invocation.Arguments.GetString("frame_id") ?? string.Empty,
                invocation.Arguments.GetString("action") ?? string.Empty,
                invocation.Arguments.GetString("element_id"),
                Text: invocation.Arguments.GetString("text"),
                Key: invocation.Arguments.GetString("key"),
                ScrollDelta: invocation.Arguments.GetInt32("scroll_delta"),
                ImageX: GetNullableInt(invocation, "image_x"),
                ImageY: GetNullableInt(invocation, "image_y"),
                EndImageX: GetNullableInt(invocation, "end_image_x"),
                EndImageY: GetNullableInt(invocation, "end_image_y"));
            var observation = await host.InteractAsync(request, ct).ConfigureAwait(false);
            return ComputerUseToolHelper.FromObservation("Desktop action completed", observation);
        }, cancellationToken);

    private static int? GetNullableInt(ToolInvocation invocation, string name) =>
        invocation.Arguments.TryGetInt32(name, out var value) ? value : null;
}

public sealed class ComputerWaitTool(IComputerUseAutomationHost host) : IAgentTool, IComputerUseTool
{
    public ToolDefinition Definition { get; } = new(
        "computer_wait",
        "Wait for a desktop condition: element_appear, element_disappear, window_title, or screen_stable. Use this instead of fixed sleeps.",
        ToolSchema.Object()
            .String(
                "condition",
                "element_appear | element_disappear | window_title | screen_stable",
                required: true,
                enumValues: ["element_appear", "element_disappear", "window_title", "screen_stable"])
            .String("element_id", "Element id to wait for.")
            .String("name", "Accessible name to wait for.")
            .String("window_title", "Window title substring.")
            .Integer("timeout_ms", "Timeout in milliseconds.", defaultValue: 5000, minimum: 200, maximum: 30000)
            .Build(),
        Source: "computer-use");

    public Task<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default) =>
        ComputerUseToolHelper.InvokeAsync(async ct =>
        {
            var result = await host.WaitAsync(
                new ComputerUseWaitRequest(
                    invocation.Arguments.GetString("condition") ?? string.Empty,
                    invocation.Arguments.GetString("element_id"),
                    invocation.Arguments.GetString("name"),
                    invocation.Arguments.GetString("window_title"),
                    invocation.Arguments.GetInt32("timeout_ms", 5000)),
                ct).ConfigureAwait(false);
            return ToolResult.Success("Desktop wait completed", result);
        }, cancellationToken);
}

public static class ComputerUseToolHelper
{
    public static async Task<ToolResult> InvokeAsync(
        Func<CancellationToken, Task<ToolResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Failure("Computer Use failed", FormatError(ex));
        }
    }

    public static string FormatError(Exception ex)
    {
        var (code, message, hint) = MapError(ex);
        return JsonSerializer.Serialize(new { code, message, hint });
    }

    public static ToolResult FromObservation(string summary, ComputerUseObservation observation)
    {
        var content = BuildObservationJson(observation);
        return ToolResult.Success(
            summary,
            content,
            imageAttachments: observation.Screenshot is null ? null : [observation.Screenshot]);
    }

    /// <summary>
    /// Serializes the observation envelope. The <c>ui_tree</c> is emitted verbatim (already one node
    /// per line) while the envelope stays indented, which keeps node data readable without the
    /// per-property indentation that used to dominate the payload size. Coordinate rules live in the
    /// Computer Use system prompt instead of being repeated in every single result.
    /// </summary>
    private static string BuildObservationJson(ComputerUseObservation observation)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("frame_id", observation.FrameId);

            writer.WritePropertyName("desktop");
            writer.WriteStartObject();
            writer.WriteNumber("left", observation.Left);
            writer.WriteNumber("top", observation.Top);
            writer.WriteNumber("width", observation.Width);
            writer.WriteNumber("height", observation.Height);
            writer.WriteNumber("dpi_scale", observation.DpiScale);
            writer.WriteEndObject();

            writer.WritePropertyName("image");
            writer.WriteStartObject();
            writer.WriteNumber("width", observation.ImageWidth);
            writer.WriteNumber("height", observation.ImageHeight);
            writer.WriteEndObject();

            writer.WritePropertyName("cursor");
            writer.WriteStartObject();
            writer.WriteNumber("x", observation.CursorX);
            writer.WriteNumber("y", observation.CursorY);
            writer.WriteEndObject();

            writer.WritePropertyName("foreground_window");
            writer.WriteStartObject();
            writer.WriteString("title", observation.ForegroundWindowTitle);
            writer.WriteString("process", observation.ForegroundProcessName);
            writer.WriteEndObject();

            if (observation.AppliedAction is null)
            {
                writer.WriteNull("action");
            }
            else
            {
                writer.WritePropertyName("action");
                WriteActionPayload(writer, observation);
            }

            if (!string.IsNullOrWhiteSpace(observation.ResolvedVia))
            {
                writer.WriteString("resolved_via", observation.ResolvedVia);
            }

            writer.WritePropertyName("ui_tree");
            WriteUiTree(writer, observation.UiTreeJson);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Writes the UI tree without re-indenting it. The host already produces one node per line, so
    /// the text is valid JSON that can be passed through with <see cref="Utf8JsonWriter.WriteRawValue(string, bool)"/>.
    /// Falls back to a JSON string when the payload is not valid JSON.
    /// </summary>
    private static void WriteUiTree(Utf8JsonWriter writer, string? uiTreeJson)
    {
        if (string.IsNullOrWhiteSpace(uiTreeJson))
        {
            writer.WriteStartArray();
            writer.WriteEndArray();
            return;
        }

        var trimmed = uiTreeJson.TrimStart();
        if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
        {
            try
            {
                writer.WriteRawValue(uiTreeJson, skipInputValidation: false);
                return;
            }
            catch (JsonException)
            {
                // Fall through to the string form below.
            }
        }

        writer.WriteStringValue(uiTreeJson);
    }

    private static void WriteActionPayload(Utf8JsonWriter writer, ComputerUseObservation observation)
    {
        writer.WriteStartObject();
        writer.WriteString("name", observation.AppliedAction);
        if (observation.UsedElementId is null)
        {
            writer.WriteNull("used_element_id");
        }
        else
        {
            writer.WriteString("used_element_id", observation.UsedElementId);
        }

        if (observation.ResolvedX is not int rx || observation.ResolvedY is not int ry)
        {
            writer.WriteNull("resolved_point");
            writer.WriteEndObject();
            return;
        }

        var (imageX, imageY) = ComputerUseCoordinateMapper.PhysicalToImage(
            rx,
            ry,
            observation.Left,
            observation.Top,
            observation.Width,
            observation.Height,
            observation.ImageWidth,
            observation.ImageHeight);

        writer.WritePropertyName("resolved_point");
        writer.WriteStartObject();
        writer.WriteNumber("physical_x", rx);
        writer.WriteNumber("physical_y", ry);
        writer.WriteNumber("image_x", imageX);
        writer.WriteNumber("image_y", imageY);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static (string Code, string Message, string Hint) MapError(Exception ex) =>
        ex switch
        {
            ComputerUseException cu => (cu.Code, cu.Message, cu.Hint),
            ArgumentException => ("invalid_args", ex.Message, "Fix the tool arguments and retry."),
            TimeoutException => ("uia_timeout", ex.Message, "call computer_observe"),
            _ when ex.Message.Contains("stale_frame", StringComparison.OrdinalIgnoreCase) =>
                ("stale_frame", ex.Message, "call computer_observe"),
            _ when ex.Message.Contains("outside the observed monitor", StringComparison.OrdinalIgnoreCase) =>
                ("off_monitor", ex.Message, "call computer_observe"),
            _ => ("tool.execution_failed", ex.Message, "call computer_observe")
        };
}
