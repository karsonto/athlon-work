using System.Text.Json;
using Athlon.Agent.Core;
using Athlon.Agent.Core.ComputerUse;

namespace Athlon.Agent.Infrastructure.ComputerUse;

public sealed class ComputerObserveTool(IComputerUseAutomationHost host) : IAgentTool, IComputerUseTool
{
    public ToolDefinition Definition { get; } = new(
        "computer_observe",
        "Capture the current desktop and return a screenshot, frame id, foreground window, cursor, display geometry, and an optional bounded UI Automation tree. UI nodes keep physical bounds and also include image_bounds relative to the screenshot. Always observe before interacting.",
        ToolSchema.Object()
            .Boolean("include_ui_tree", "Include the bounded Windows UI Automation tree.", defaultValue: true)
            .Integer("max_tree_depth", "Maximum UI tree depth (1-10).", defaultValue: 4, minimum: 1, maximum: 10)
            .Integer("max_nodes", "Maximum UI nodes (20-1000).", defaultValue: 80, minimum: 20, maximum: 1000)
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
                    invocation.Arguments.GetInt32("max_tree_depth", 4),
                    invocation.Arguments.GetInt32("max_nodes", 80)),
                ct).ConfigureAwait(false);
            return ComputerUseToolHelper.FromObservation("Desktop observed", observation);
        }, cancellationToken);
}

public sealed class ComputerInteractTool(IComputerUseAutomationHost host) : IAgentTool, IComputerUseTool
{
    public ToolDefinition Definition { get; } = new(
        "computer_interact",
        "Perform exactly one desktop action using a fresh frame. For click/double_click/right_click/scroll/drag prefer image_x/image_y in screenshot pixels (host maps them to the physical desktop). Use element_id mainly to focus controls for typing. The result includes a post-action screenshot and a fresh frame id; call computer_observe when you need an updated UI tree.",
        ToolSchema.Object()
            .String("frame_id", "Latest frame id returned by computer_observe.", required: true, minLength: 1)
            .String(
                "action",
                "One action: click, double_click, right_click, type_text, key, hotkey, scroll, or drag.",
                required: true,
                enumValues: ["click", "double_click", "right_click", "type_text", "key", "hotkey", "scroll", "drag"])
            .String("element_id", "UI Automation element id. Preferred for focusing type_text/key/hotkey; optional for pointer actions when image coordinates are unavailable.")
            .Integer("image_x", "X in screenshot pixels (0 .. image.width-1). Required for accurate pointer actions.")
            .Integer("image_y", "Y in screenshot pixels (0 .. image.height-1). Required for accurate pointer actions.")
            .Integer("end_image_x", "Drag destination X in screenshot pixels.")
            .Integer("end_image_y", "Drag destination Y in screenshot pixels.")
            .Integer("x", "Fallback physical desktop X coordinate. Do not use screenshot pixels here.")
            .Integer("y", "Fallback physical desktop Y coordinate. Do not use screenshot pixels here.")
            .Integer("end_x", "Drag destination physical desktop X coordinate.")
            .Integer("end_y", "Drag destination physical desktop Y coordinate.")
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
                GetNullableInt(invocation, "x"),
                GetNullableInt(invocation, "y"),
                GetNullableInt(invocation, "end_x"),
                GetNullableInt(invocation, "end_y"),
                invocation.Arguments.GetString("text"),
                invocation.Arguments.GetString("key"),
                invocation.Arguments.GetInt32("scroll_delta"),
                GetNullableInt(invocation, "image_x"),
                GetNullableInt(invocation, "image_y"),
                GetNullableInt(invocation, "end_image_x"),
                GetNullableInt(invocation, "end_image_y"));
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
        var content = JsonSerializer.Serialize(new
        {
            frame_id = observation.FrameId,
            desktop = new
            {
                observation.Left,
                observation.Top,
                observation.Width,
                observation.Height,
                dpi_scale = observation.DpiScale
            },
            image = new
            {
                width = observation.ImageWidth,
                height = observation.ImageHeight
            },
            coordinate_hint =
                "For pointer actions pass image_x/image_y in screenshot pixels (0..image.width-1, 0..image.height-1). Use image_bounds centers when helpful — never UI tree bounds (those are physical). The host maps screenshot pixels to the physical desktop using capture vs image size; do not multiply by dpi_scale. Physical x/y are fallback only. element_id is for focusing type_text/key/hotkey; when both element_id and image_x/image_y are set for a click, image coordinates win.",
            cursor = new { x = observation.CursorX, y = observation.CursorY },
            foreground_window = new
            {
                title = observation.ForegroundWindowTitle,
                process = observation.ForegroundProcessName
            },
            action = observation.AppliedAction is null
                ? null
                : BuildActionPayload(observation),
            ui_tree = ParseUiTree(observation.UiTreeJson)
        }, new JsonSerializerOptions { WriteIndented = true });

        return ToolResult.Success(
            summary,
            content,
            imageAttachments: [observation.Screenshot]);
    }

    private static object? BuildActionPayload(ComputerUseObservation observation)
    {
        if (observation.ResolvedX is not int rx || observation.ResolvedY is not int ry)
        {
            return new
            {
                name = observation.AppliedAction,
                used_element_id = observation.UsedElementId,
                resolved_point = (object?)null
            };
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

        return new
        {
            name = observation.AppliedAction,
            used_element_id = observation.UsedElementId,
            resolved_point = new
            {
                physical_x = rx,
                physical_y = ry,
                image_x = imageX,
                image_y = imageY
            }
        };
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

    private static object? ParseUiTree(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
