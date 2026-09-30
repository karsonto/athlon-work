using System.Security.Cryptography;
using System.Windows.Automation;
using Athlon.Agent.Core;
using Athlon.Agent.Core.ComputerUse;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.ComputerUse;

/// <summary>Pointer, keyboard and element-native interactions.</summary>
public sealed partial class ComputerUseAutomationHost
{
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

            var hasElementId = !string.IsNullOrWhiteSpace(request.ElementId);
            AutomationElement? target = null;
            if (hasElementId)
            {
                if (!frame.Elements.TryGetValue(request.ElementId!, out target))
                {
                    throw new ComputerUseException(
                        "unknown_element",
                        $"Unknown element_id '{request.ElementId}' for frame '{request.FrameId}'.");
                }
            }

            var hasImagePoint = request.ImageX is not null && request.ImageY is not null;
            var hasPhysicalPoint = request.X is not null && request.Y is not null;
            var isPointerAction = request.Action is ("click" or "double_click" or "right_click" or "scroll" or "drag");
            if (isPointerAction
                && target is null
                && !hasImagePoint
                && !hasPhysicalPoint)
            {
                throw new ComputerUseException(
                    "invalid_args",
                    $"{request.Action} requires element_id, image_x/image_y, or physical x/y coordinates.");
            }

            if (hasImagePoint)
            {
                EnsureImagePointInFrame(
                    request.ImageX!.Value,
                    request.ImageY!.Value,
                    frame.ImageWidth,
                    frame.ImageHeight,
                    "image_x/image_y");
            }

            if (request.EndImageX is int endImageX && request.EndImageY is int endImageY)
            {
                EnsureImagePointInFrame(
                    endImageX,
                    endImageY,
                    frame.ImageWidth,
                    frame.ImageHeight,
                    "end_image_x/end_image_y");
            }

            var useImageForPointer = isPointerAction
                && ComputerUsePointerTargetPolicy.PreferImagePoint(hasElementId, hasImagePoint);
            var useElementForPointer = isPointerAction
                && ComputerUsePointerTargetPolicy.PreferElementClickablePoint(hasElementId, hasImagePoint);
            // Typing still focuses via element when available, even if image coords were also sent.
            var useElementForTyping = !isPointerAction
                && target is not null
                && request.Action is "type_text" or "key" or "hotkey";

            // Pre-action telemetry: which channel the arguments imply before any UIA probe. The
            // element-native attempt can upgrade this, so `actualStrategy` tracks what really ran.
            var resolveStrategy = ComputerUseResolveStrategyClassifier.Classify(
                isPointerAction,
                hasElementId,
                hasImagePoint,
                hasPhysicalPoint);
            var actualStrategy = resolveStrategy;
            // Set inside the overlay-hidden closure, then read by the audit log and observation.
            var usedElementNative = false;

            int resolvedX = 0;
            int resolvedY = 0;
            int? resolvedEndX = request.EndX;
            int? resolvedEndY = request.EndY;
            string? usedElementId = useElementForPointer || useElementForTyping
                ? request.ElementId
                : null;

            var overlayHiddenAt = ComputerUseTiming.Stamp();
            var phaseTimings = new ComputerUsePhaseTimings();
            var result = await overlayRegistry.RunWithOverlayHiddenAsync(async ct =>
            {
                ct.ThrowIfCancellationRequested();
                phaseTimings.OverlayHideMs = ComputerUseTiming.ElapsedMs(overlayHiddenAt);
                // Validate against the observed monitor, not wherever the cursor drifted.
                var monitorX = frame.Left + Math.Max(0, frame.Width / 2);
                var monitorY = frame.Top + Math.Max(0, frame.Height / 2);
                var currentDisplay = captureService.ProbeAt(monitorX, monitorY);
                var currentForeground = await RunBoundedUiAutomationAsync(
                    uiAutomationService.GetForegroundWindowIdentity,
                    ct).ConfigureAwait(false);
                // Window- and monitor-targeted frames intentionally point somewhere other than the
                // cursor monitor, so the cursor-relative freshness gates are skipped for them.
                if (ComputerUseFrameFreshness.RequiresCursorRelativeGates(frame.ObserveTarget is not null)
                    && !ComputerUseFrameFreshness.MatchesMonitor(
                        frame.Left,
                        frame.Top,
                        frame.Width,
                        frame.Height,
                        currentDisplay.Left,
                        currentDisplay.Top,
                        currentDisplay.Width,
                        currentDisplay.Height))
                {
                    _latestFrame = null;
                    throw new ComputerUseException(
                        "stale_frame",
                        "The visible desktop changed since observation.");
                }

                if (ComputerUseFrameFreshness.RequiresCursorRelativeGates(frame.ObserveTarget is not null)
                    && !ComputerUseFrameFreshness.MatchesForegroundWindow(
                        frame.ForegroundWindowHandle,
                        currentForeground.Handle,
                        frame.ForegroundProcessName,
                        currentForeground.ProcessName))
                {
                    _latestFrame = null;
                    throw new ComputerUseException(
                        "stale_frame",
                        "The foreground window changed since observation.");
                }

                var x = 0;
                var y = 0;
                int? endX = null;
                int? endY = null;

                // Element-native channel: when only an element_id was supplied and the action has a
                // native form, drive the control through its own UI Automation pattern. The pointer
                // never moves, so the user's cursor is untouched. Falls through to coordinate input
                // when the control exposes no usable pattern.
                usedElementNative = await TryRunElementNativeAsync(
                    request,
                    target,
                    hasElementId,
                    hasImagePoint,
                    hasPhysicalPoint,
                    isPointerAction,
                    ct).ConfigureAwait(false);
                if (usedElementNative)
                {
                    actualStrategy = ComputerUseResolveStrategy.ElementNative;
                    // Native actions change layout too (Invoke navigates), so settle before capturing.
                    return await SettleAndCaptureAsync().ConfigureAwait(false);
                }

                if ((useElementForPointer || useElementForTyping)
                    && target is not null)
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
                else if (isPointerAction)
                {
                    if (useImageForPointer)
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
                }

                if (request.Action == "drag")
                {
                    (endX, endY) = ResolveDragEnd(request, frame);
                    if (!ComputerUseFrameFreshness.ContainsPoint(
                            frame.Left,
                            frame.Top,
                            frame.Width,
                            frame.Height,
                            endX.Value,
                            endY.Value))
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
                var inputStarted = ComputerUseTiming.Stamp();
                if (useElementForTyping)
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
                phaseTimings.InputMs = ComputerUseTiming.ElapsedMs(inputStarted);
                return await SettleAndCaptureAsync().ConfigureAwait(false);

                // Shared tail for both channels: wait for the desktop to stop changing, then decide
                // whether a screenshot is worth the tokens. Declared as a local function so the
                // element-native early return and the coordinate path settle identically.
                async Task<CapturedState?> SettleAndCaptureAsync()
                {
                    // Once input starts, complete observation even if the caller cancels; never report a
                    // cancellable half-action that could be retried against the same frame.
                    var settleStarted = ComputerUseTiming.Stamp();
                    try
                    {
                        await ComputerUsePostActionSettler.WaitForStableAsync(
                            _ => Task.FromResult(captureService.CaptureSignatureAt(monitorX, monitorY)),
                            CancellationToken.None,
                            minimumSamples: IsLayoutNeutralAction(request.Action)
                                ? _settings.SettleKeyboardMinimumSamples
                                : _settings.SettleMinimumSamples,
                            maxSamples: _settings.SettleMaxSamples,
                            sampleInterval: TimeSpan.FromMilliseconds(_settings.SettleSampleIntervalMs),
                            requiredConsecutiveMatches: _settings.SettleRequiredConsecutiveMatches)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Stability probing is an optimization. Preserve the previous safe delay
                        // when a display driver cannot provide sampled pixels.
                        await Task.Delay(_settings.SettleFallbackDelayMs, CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    phaseTimings.SettleMs = ComputerUseTiming.ElapsedMs(settleStarted);

                    // Keyboard-only actions do not reflow the window, so the model can usually predict
                    // the outcome. Returning a screenshot for every keystroke is pure token cost; offer
                    // computer_observe for callers that need visual confirmation.
                    if (_settings.SkipScreenshotForKeyboardActions
                        && request.Action is "type_text" or "key" or "hotkey")
                    {
                        return null;
                    }

                    // Return a fresh screenshot + frame only. A shallow post-action UI tree pushed
                    // models onto coarse element_id clicks; prefer image_x/image_y next, and
                    // computer_observe when a full tree is needed.
                    var captureStarted = ComputerUseTiming.Stamp();
                    var captured = await CaptureStateAsync(
                        includeUiTree: false,
                        maxDepth: 1,
                        maxNodes: 1,
                        CancellationToken.None,
                        monitorX,
                        monitorY).ConfigureAwait(false);
                    phaseTimings.CaptureMs = ComputerUseTiming.ElapsedMs(captureStarted);
                    return captured;
                }
            }, cancellationToken).ConfigureAwait(false);

            phaseTimings.TotalMs = ComputerUseTiming.ElapsedMs(overlayHiddenAt);

            await auditLog.WriteAsync(
                "computer_interact",
                new
                {
                    request.Action,
                    request.FrameId,
                    request.ElementId,
                    used_element_id = usedElementId,
                    used_image_point = useImageForPointer,
                    // Element-native actions never resolve a coordinate, so report null rather than a
                    // misleading 0,0 in the audit trail.
                    resolved_x = usedElementNative ? (int?)null : resolvedX,
                    resolved_y = usedElementNative ? (int?)null : resolvedY,
                    end_x = resolvedEndX,
                    end_y = resolvedEndY,
                    resolved_via = ComputerUseResolveStrategyClassifier.ToWireValue(actualStrategy),
                    foreground_window = result?.Ui.ForegroundWindowTitle ?? frame.ForegroundWindowTitle,
                    timings = phaseTimings.ToPayload()
                },
                CancellationToken.None).ConfigureAwait(false);

            return await BuildObservationAsync(
                result,
                CancellationToken.None,
                appliedAction: request.Action,
                usedElementId: usedElementId,
                resolvedX: usedElementNative ? null : resolvedX,
                resolvedY: usedElementNative ? null : resolvedY,
                resolvedVia: ComputerUseResolveStrategyClassifier.ToWireValue(actualStrategy))
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Actions that do not reflow the window: typing, single keys, and scrolling. These keep the
    /// short settle budget because a stable verdict arrives much sooner.
    /// </summary>
    private static bool IsLayoutNeutralAction(string action) =>
        ComputerUseActionKinds.IsLayoutNeutralAction(action);

    /// <summary>
    /// Runs the element-native channel when the arguments select it. Returns false when the action
    /// has no native form, no element was supplied, pixel coordinates were given, or the control
    /// exposes no usable pattern — in every one of those cases the caller falls back to coordinate
    /// input so no functionality is lost.
    /// </summary>
    private async Task<bool> TryRunElementNativeAsync(
        ComputerUseInteractRequest request,
        AutomationElement? target,
        bool hasElementId,
        bool hasImagePoint,
        bool hasPhysicalPoint,
        bool isPointerAction,
        CancellationToken cancellationToken)
    {
        if (target is null || hasImagePoint)
        {
            return false;
        }

        // Only the pure "element_id and nothing else" shape activates the native channel. Pixels
        // still win, and explicit physical coordinates mean the caller wants real pointer input.
        if (ComputerUseResolveStrategyClassifier.Resolve(
                request.Action,
                hasElementId,
                hasImagePoint,
                hasPhysicalPoint) != ComputerUseResolveStrategy.ElementNative)
        {
            return false;
        }

        if (isPointerAction && !ComputerUseResolveStrategyClassifier.HasElementNativeForm(request.Action))
        {
            return false;
        }

        var outcome = await RunBoundedUiAutomationAsync(
            () => uiAutomationService.TryExecuteElementAction(
                target,
                request.Action,
                request.Key,
                request.ScrollDelta,
                request.Text),
            cancellationToken).ConfigureAwait(false);
        return outcome.Applied;
    }

    private static void EnsureImagePointInFrame(
        int imageX,
        int imageY,
        int imageWidth,
        int imageHeight,
        string parameterName)
    {
        if (ComputerUseCoordinateMapper.IsImagePointInFrame(imageX, imageY, imageWidth, imageHeight))
        {
            return;
        }

        throw new ComputerUseException(
            "invalid_args",
            $"{parameterName} must be screenshot pixels in [0,{imageWidth}) x [0,{imageHeight}). "
            + "Do not pass UI tree bounds (physical) or dpi-scaled values as image coordinates.");
    }
}
