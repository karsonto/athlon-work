using System.Security.Cryptography;
using System.Windows.Automation;
using Athlon.Agent.Core;
using Athlon.Agent.Core.ComputerUse;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.ComputerUse;

public sealed class ComputerUseAutomationHost(
    ComputerUseCaptureService captureService,
    ComputerUseUiAutomationService uiAutomationService,
    ComputerUseInputService inputService,
    ComputerUseOverlayRegistry overlayRegistry,
    IImageAttachmentStore imageAttachmentStore,
    IAgentRunContextAccessor runContextAccessor,
    AuditLogService auditLog) : IComputerUseAutomationHost
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _uiaSlots = new(4, 4);
    private FrameState? _latestFrame;

    public async Task<ComputerUseObservation> ObserveAsync(
        ComputerUseObserveRequest request,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var observation = await ObserveCoreAsync(request, cancellationToken).ConfigureAwait(false);
            await auditLog.WriteAsync(
                "computer_observe",
                new
                {
                    observation.FrameId,
                    observation.Left,
                    observation.Top,
                    observation.Width,
                    observation.Height,
                    observation.ImageWidth,
                    observation.ImageHeight,
                    observation.ForegroundWindowTitle,
                    observation.ForegroundProcessName
                },
                cancellationToken).ConfigureAwait(false);
            return observation;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ComputerUseObservation> InteractAsync(
        ComputerUseInteractRequest request,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = runContextAccessor.Current
                ?? throw new ComputerUseException(
                    "invalid_args",
                    "Computer Use requires an active agent run context.");
            var frame = _latestFrame;
            if (frame is null
                || !string.Equals(request.FrameId, frame.FrameId, StringComparison.Ordinal)
                || !string.Equals(run.SessionId, frame.SessionId, StringComparison.Ordinal)
                || !string.Equals(run.RunId, frame.RunId, StringComparison.Ordinal))
            {
                throw new ComputerUseException(
                    "stale_frame",
                    "The desktop changed or this frame is not current.");
            }

            if (!ComputerUseFrameFreshness.IsWithinAge(frame.CreatedAt, DateTimeOffset.UtcNow))
            {
                _latestFrame = null;
                throw new ComputerUseException(
                    "stale_frame",
                    "The observation expired.");
            }

            AutomationElement? target = null;
            if (!string.IsNullOrWhiteSpace(request.ElementId))
            {
                if (!frame.Elements.TryGetValue(request.ElementId, out target))
                {
                    throw new ComputerUseException(
                        "unknown_element",
                        $"Unknown element_id '{request.ElementId}' for frame '{request.FrameId}'.");
                }
            }

            var hasImagePoint = request.ImageX is not null && request.ImageY is not null;
            var hasPhysicalPoint = request.X is not null && request.Y is not null;
            if (request.Action is ("click" or "double_click" or "right_click" or "scroll" or "drag")
                && target is null
                && !hasImagePoint
                && !hasPhysicalPoint)
            {
                throw new ComputerUseException(
                    "invalid_args",
                    $"{request.Action} requires element_id, image_x/image_y, or physical x/y coordinates.");
            }

            int resolvedX = 0;
            int resolvedY = 0;
            int? resolvedEndX = request.EndX;
            int? resolvedEndY = request.EndY;

            var result = await overlayRegistry.RunWithOverlayHiddenAsync(async ct =>
            {
                ct.ThrowIfCancellationRequested();
                // Validate against the observed monitor, not wherever the cursor drifted.
                var monitorX = frame.Left + Math.Max(0, frame.Width / 2);
                var monitorY = frame.Top + Math.Max(0, frame.Height / 2);
                var currentDesktop = await CaptureStateAsync(
                    includeUiTree: false,
                    maxDepth: 1,
                    maxNodes: 20,
                    ct,
                    monitorX,
                    monitorY).ConfigureAwait(false);
                if (!ComputerUseFrameFreshness.MatchesMonitor(
                        frame.Left,
                        frame.Top,
                        frame.Width,
                        frame.Height,
                        currentDesktop.Desktop.Left,
                        currentDesktop.Desktop.Top,
                        currentDesktop.Desktop.Width,
                        currentDesktop.Desktop.Height))
                {
                    _latestFrame = null;
                    throw new ComputerUseException(
                        "stale_frame",
                        "The visible desktop changed since observation.");
                }

                var x = 0;
                var y = 0;
                var endX = request.EndX;
                var endY = request.EndY;
                if (target is not null
                    && request.Action is ("click" or "double_click" or "right_click" or "scroll" or "drag"
                        or "type_text" or "key" or "hotkey"))
                {
                    // Resolve the click point after the overlay is hidden so focus/geometry are stable.
                    var point = await RunBoundedUiAutomationAsync(
                        () => uiAutomationService.TryGetClickablePoint(target, out var px, out var py)
                            ? new ClickPoint(px, py)
                            : null,
                        ct).ConfigureAwait(false);
                    if (point is null)
                    {
                        _latestFrame = null;
                        throw new ComputerUseException(
                            "element_gone",
                            $"Element '{request.ElementId}' no longer has a clickable point.");
                    }

                    x = point.X;
                    y = point.Y;
                }
                else if (request.Action is ("click" or "double_click" or "right_click" or "scroll" or "drag"))
                {
                    // Coordinate fallback: require the same foreground process and on-monitor point.
                    if (!ComputerUseFrameFreshness.MatchesForegroundProcess(
                            frame.ForegroundProcessName,
                            currentDesktop.Ui.ForegroundProcessName))
                    {
                        _latestFrame = null;
                        throw new ComputerUseException(
                            "stale_frame",
                            "The visible desktop changed since observation.");
                    }

                    if (hasImagePoint)
                    {
                        (x, y) = ComputerUseCoordinateMapper.ImageToPhysical(
                            request.ImageX!.Value,
                            request.ImageY!.Value,
                            frame.Left,
                            frame.Top,
                            frame.CaptureWidth,
                            frame.CaptureHeight,
                            frame.ImageWidth,
                            frame.ImageHeight);
                    }
                    else
                    {
                        x = request.X!.Value;
                        y = request.Y!.Value;
                    }

                    if (request.Action == "drag"
                        && request.EndImageX is int endImageX
                        && request.EndImageY is int endImageY)
                    {
                        (endX, endY) = ComputerUseCoordinateMapper.ImageToPhysical(
                            endImageX,
                            endImageY,
                            frame.Left,
                            frame.Top,
                            frame.CaptureWidth,
                            frame.CaptureHeight,
                            frame.ImageWidth,
                            frame.ImageHeight);
                    }

                    if (!ComputerUseFrameFreshness.ContainsPoint(
                            frame.Left,
                            frame.Top,
                            frame.Width,
                            frame.Height,
                            x,
                            y))
                    {
                        _latestFrame = null;
                        throw new ComputerUseException(
                            "off_monitor",
                            "Coordinates are outside the observed monitor.");
                    }

                    if (endX is int dragEndX
                        && endY is int dragEndY
                        && !ComputerUseFrameFreshness.ContainsPoint(
                            frame.Left,
                            frame.Top,
                            frame.Width,
                            frame.Height,
                            dragEndX,
                            dragEndY))
                    {
                        _latestFrame = null;
                        throw new ComputerUseException(
                            "off_monitor",
                            "Drag destination coordinates are outside the observed monitor.");
                    }
                }

                resolvedX = x;
                resolvedY = y;
                resolvedEndX = endX;
                resolvedEndY = endY;

                // Burn the frame only after freshness checks pass so false stale checks can retry.
                _latestFrame = null;

                ct.ThrowIfCancellationRequested();
                if (target is not null && request.Action is "type_text" or "key" or "hotkey")
                {
                    await inputService.ExecuteAsync(
                        "click",
                        x,
                        y,
                        null,
                        null,
                        null,
                        null,
                        0,
                        CancellationToken.None).ConfigureAwait(false);
                }

                await inputService.ExecuteAsync(
                    request.Action,
                    x,
                    y,
                    endX,
                    endY,
                    request.Text,
                    request.Key,
                    request.ScrollDelta,
                    CancellationToken.None).ConfigureAwait(false);
                // Once input starts, complete observation even if the caller cancels; never report a
                // cancellable half-action that could be retried against the same frame.
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                return await CaptureStateAsync(
                    includeUiTree: false,
                    maxDepth: 1,
                    maxNodes: 20,
                    CancellationToken.None,
                    monitorX,
                    monitorY).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            await auditLog.WriteAsync(
                "computer_interact",
                new
                {
                    request.Action,
                    request.FrameId,
                    request.ElementId,
                    resolved_x = resolvedX,
                    resolved_y = resolvedY,
                    end_x = resolvedEndX,
                    end_y = resolvedEndY,
                    foreground_window = result.Ui.ForegroundWindowTitle
                },
                CancellationToken.None).ConfigureAwait(false);

            return BuildObservation(
                result,
                appliedAction: request.Action,
                usedElementId: request.ElementId,
                resolvedX: resolvedX,
                resolvedY: resolvedY);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<string> WaitAsync(
        ComputerUseWaitRequest request,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateWaitRequest(request);
            var result = await overlayRegistry.RunWithOverlayHiddenAsync(async ct =>
            {
                var timeout = TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMs, 200, 30000));
                var deadline = DateTimeOffset.UtcNow + timeout;
                string? previousHash = null;
                var stableSamples = 0;

                while (DateTimeOffset.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    var matched = await EvaluateWaitConditionAsync(
                        request,
                        previousHash,
                        stableSamples,
                        ct).ConfigureAwait(false);
                    previousHash = matched.Hash;
                    stableSamples = matched.StableSamples;

                    if (matched.Satisfied)
                    {
                        return $"Condition '{request.Condition}' satisfied.";
                    }

                    await Task.Delay(200, ct).ConfigureAwait(false);
                }

                throw new TimeoutException(
                    $"Timed out after {(int)timeout.TotalMilliseconds} ms waiting for '{request.Condition}'.");
            }, cancellationToken).ConfigureAwait(false);
            await auditLog.WriteAsync(
                "computer_wait",
                new
                {
                    request.Condition,
                    request.ElementId,
                    request.Name,
                    request.WindowTitle,
                    request.TimeoutMs
                },
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<ComputerUseObservation> ObserveCoreAsync(
        ComputerUseObserveRequest request,
        CancellationToken cancellationToken)
    {
        var result = await overlayRegistry.RunWithOverlayHiddenAsync(
            ct => CaptureStateAsync(
                request.IncludeUiTree,
                Math.Clamp(request.MaxTreeDepth, 1, 10),
                Math.Clamp(request.MaxNodes, 20, 1000),
                ct),
            cancellationToken).ConfigureAwait(false);
        return BuildObservation(result);
    }

    private CapturedState CaptureState(
        bool includeUiTree,
        int maxDepth,
        int maxNodes,
        int? monitorX = null,
        int? monitorY = null)
    {
        var desktop = monitorX is int x && monitorY is int y
            ? captureService.CaptureAt(x, y)
            : captureService.CaptureCursorMonitor();
        var foreground = uiAutomationService.Capture(
            includeUiTree ? maxDepth : 1,
            includeUiTree ? maxNodes : 1,
            desktop.Left,
            desktop.Top,
            desktop.Width,
            desktop.Height);
        var ui = includeUiTree
            ? foreground
            : foreground with
            {
                Json = "[]",
                Elements = new Dictionary<string, AutomationElement>()
            };
        return new CapturedState(desktop, ui);
    }

    private Task<CapturedState> CaptureStateAsync(
        bool includeUiTree,
        int maxDepth,
        int maxNodes,
        CancellationToken cancellationToken,
        int? monitorX = null,
        int? monitorY = null) =>
        RunBoundedUiAutomationAsync(
            () => CaptureState(includeUiTree, maxDepth, maxNodes, monitorX, monitorY),
            cancellationToken);

    private async Task<T> RunBoundedUiAutomationAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        if (!await _uiaSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new ComputerUseException(
                "uia_timeout",
                "Windows UI Automation is unavailable because prior providers are still unresponsive.");
        }

        var task = Task.Run(action, CancellationToken.None);
        _ = task.ContinueWith(
            _ => _uiaSlots.Release(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            return await task
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ComputerUseException(
                "uia_timeout",
                "Windows UI Automation did not respond within 5 seconds.");
        }
    }

    private ComputerUseObservation BuildObservation(
        CapturedState state,
        string? appliedAction = null,
        string? usedElementId = null,
        int? resolvedX = null,
        int? resolvedY = null)
    {
        var sessionId = runContextAccessor.Current?.SessionId;
        var runId = runContextAccessor.Current?.RunId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(runId))
        {
            throw new ComputerUseException(
                "invalid_args",
                "Computer Use requires an active agent run context.");
        }

        var frameId = $"frame_{Guid.NewGuid():N}";
        var extension = state.Desktop.MimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            ? ".jpg"
            : ".png";
        var screenshot = imageAttachmentStore.SaveBytes(
            sessionId,
            $"{frameId}{extension}",
            state.Desktop.MimeType,
            state.Desktop.ImageBytes);
        _latestFrame = new FrameState(
            frameId,
            sessionId,
            runId,
            state.Desktop.Left,
            state.Desktop.Top,
            state.Desktop.Width,
            state.Desktop.Height,
            state.Desktop.Width,
            state.Desktop.Height,
            state.Desktop.ImageWidth,
            state.Desktop.ImageHeight,
            state.Ui.ForegroundWindowTitle,
            state.Ui.ForegroundProcessName,
            DateTimeOffset.UtcNow,
            state.Ui.Elements);

        return new ComputerUseObservation(
            frameId,
            screenshot,
            state.Desktop.Left,
            state.Desktop.Top,
            state.Desktop.Width,
            state.Desktop.Height,
            state.Desktop.DpiScale,
            state.Desktop.CursorX,
            state.Desktop.CursorY,
            state.Ui.ForegroundWindowTitle,
            state.Ui.ForegroundProcessName,
            state.Ui.Json,
            state.Desktop.ImageWidth,
            state.Desktop.ImageHeight,
            appliedAction,
            usedElementId,
            resolvedX,
            resolvedY);
    }

    private bool IsScreenStable(ref string? previousHash, ref int stableSamples)
    {
        var bytes = captureService.CaptureCursorMonitor().ImageBytes;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (string.Equals(previousHash, hash, StringComparison.Ordinal))
        {
            stableSamples++;
        }
        else
        {
            previousHash = hash;
            stableSamples = 0;
        }

        return stableSamples >= 2;
    }

    private async Task<WaitEvaluation> EvaluateWaitConditionAsync(
        ComputerUseWaitRequest request,
        string? previousHash,
        int stableSamples,
        CancellationToken cancellationToken)
    {
        if (request.Condition == "screen_stable")
        {
            var satisfied = IsScreenStable(ref previousHash, ref stableSamples);
            return new WaitEvaluation(satisfied, previousHash, stableSamples);
        }

        var matched = request.Condition switch
        {
            "element_appear" => await RunBoundedUiAutomationAsync(
                () => MatchesElement(request, expectAvailable: true),
                cancellationToken).ConfigureAwait(false),
            "element_disappear" => await RunBoundedUiAutomationAsync(
                () => MatchesElement(request, expectAvailable: false),
                cancellationToken).ConfigureAwait(false),
            "window_title" => await RunBoundedUiAutomationAsync(
                () => uiAutomationService.GetForegroundWindowTitle()
                    .Contains(
                        request.WindowTitle!,
                        StringComparison.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false),
            _ => throw new ComputerUseException(
                "invalid_args",
                $"Unsupported wait condition '{request.Condition}'.")
        };
        return new WaitEvaluation(matched, previousHash, stableSamples);
    }

    private bool MatchesElement(ComputerUseWaitRequest request, bool expectAvailable)
    {
        bool available;
        if (!string.IsNullOrWhiteSpace(request.ElementId))
        {
            var run = runContextAccessor.Current;
            var frame = _latestFrame;
            if (run is null
                || frame is null
                || !string.Equals(run.SessionId, frame.SessionId, StringComparison.Ordinal)
                || !string.Equals(run.RunId, frame.RunId, StringComparison.Ordinal)
                || !frame.Elements.TryGetValue(request.ElementId, out var element))
            {
                throw new ComputerUseException(
                    "unknown_element",
                    $"Element_id '{request.ElementId}' is not available for the current Computer Use turn.");
            }

            available = ComputerUseUiAutomationService.IsAvailable(element!);
        }
        else
        {
            available = uiAutomationService.MatchesCurrentDesktop(null, request.Name);
        }

        return expectAvailable ? available : !available;
    }

    private static void ValidateWaitRequest(ComputerUseWaitRequest request)
    {
        switch (request.Condition)
        {
            case "element_appear":
            case "element_disappear":
                if (string.IsNullOrWhiteSpace(request.ElementId)
                    && string.IsNullOrWhiteSpace(request.Name))
                {
                    throw new ComputerUseException(
                        "invalid_args",
                        $"{request.Condition} requires element_id or name.");
                }
                break;
            case "window_title":
                if (string.IsNullOrWhiteSpace(request.WindowTitle))
                {
                    throw new ComputerUseException(
                        "invalid_args",
                        "window_title requires a non-empty window_title.");
                }
                break;
            case "screen_stable":
                break;
            default:
                throw new ComputerUseException(
                    "invalid_args",
                    $"Unsupported wait condition '{request.Condition}'.");
        }
    }

    private sealed record CapturedState(
        ComputerUseCapturedDesktop Desktop,
        ComputerUseUiSnapshot Ui);

    private sealed record FrameState(
        string FrameId,
        string SessionId,
        string RunId,
        int Left,
        int Top,
        int Width,
        int Height,
        int CaptureWidth,
        int CaptureHeight,
        int ImageWidth,
        int ImageHeight,
        string ForegroundWindowTitle,
        string ForegroundProcessName,
        DateTimeOffset CreatedAt,
        IReadOnlyDictionary<string, AutomationElement> Elements);

    private sealed record ClickPoint(int X, int Y);

    private sealed record WaitEvaluation(
        bool Satisfied,
        string? Hash,
        int StableSamples);
}
