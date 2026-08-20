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
        "Perform exactly one desktop action using a fresh frame. Prefer element_id; otherwise use image_x/image_y relative to the frame screenshot. Physical x/y are fallback only. The result includes a post-action screenshot, a fresh frame id, and a bounded UI tree for the next action.",
        ToolSchema.Object()
            .String("frame_id", "Latest frame id returned by computer_observe.", required: true, minLength: 1)
            .String(
                "action",
                "One action: click, double_click, right_click, type_text, key, hotkey, scroll, or drag.",
                required: true,
                enumValues: ["click", "double_click", "right_click", "type_text", "key", "hotkey", "scroll", "drag"])
            .String("element_id", "Preferred UI Automation element id from the latest frame.")
            .Integer("image_x", "X coordinate relative to the top-left of the frame screenshot.")
            .Integer("image_y", "Y coordinate relative to the top-left of the frame screenshot.")
            .Integer("end_image_x", "Drag destination X relative to the frame screenshot.")
            .Integer("end_image_y", "Drag destination Y relative to the frame screenshot.")
            .Integer("x", "Fallback physical desktop X coordinate.")
            .Integer("y", "Fallback physical desktop Y coordinate.")
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
                "Prefer element_id. UI tree bounds are physical desktop coordinates; image_bounds are relative to this screenshot. Otherwise pass image_x/image_y relative to the screenshot (top-left origin). Physical x/y are fallback only.",
            cursor = new { x = observation.CursorX, y = observation.CursorY },
            foreground_window = new
            {
                title = observation.ForegroundWindowTitle,
                process = observation.ForegroundProcessName
            },
            action = observation.AppliedAction is null
                ? null
                : new
                {
                    name = observation.AppliedAction,
                    used_element_id = observation.UsedElementId,
                    resolved_point = observation.ResolvedX is int rx && observation.ResolvedY is int ry
                        ? new { x = rx, y = ry }
                        : null
                },
            ui_tree = ParseUiTree(observation.UiTreeJson)
        }, new JsonSerializerOptions { WriteIndented = true });

        return ToolResult.Success(
            summary,
            content,
            imageAttachments: [observation.Screenshot]);
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
