using System.Security.Cryptography;
using System.Windows.Automation;
using Athlon.Agent.Core;
using Athlon.Agent.Core.ComputerUse;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.ComputerUse;

/// <summary>Desktop capture and observation assembly.</summary>
public sealed partial class ComputerUseAutomationHost
{
    private async Task<ComputerUseObservation> ObserveCoreAsync(
        ComputerUseObserveRequest request,
        ComputerUsePhaseTimings phaseTimings,
        CancellationToken cancellationToken)
    {
        var maxDepth = Math.Clamp(
            request.MaxTreeDepth,
            ComputerUseObservationLimits.MinTreeDepth,
            ComputerUseObservationLimits.MaxTreeDepth);
        var maxNodes = Math.Clamp(
            request.MaxNodes,
            ComputerUseObservationLimits.MinNodes,
            ComputerUseObservationLimits.MaxNodes);
        phaseTimings.MaxTreeDepth = maxDepth;
        phaseTimings.MaxNodes = maxNodes;

        var target = request.MonitorIndex is null
            && string.IsNullOrWhiteSpace(request.WindowTitle)
                ? null
                : new ComputerUseObserveTarget(
                    request.MonitorIndex,
                    request.WindowTitle,
                    request.WindowProcessName);

        var captureStarted = ComputerUseTiming.Stamp();
        var result = await overlayRegistry.RunWithOverlayHiddenAsync(
            ct => CaptureStateAsync(
                request.IncludeUiTree,
                maxDepth,
                maxNodes,
                ct,
                target: target),
            cancellationToken).ConfigureAwait(false);
        phaseTimings.OverlayHideMs = ComputerUseTiming.ElapsedMs(captureStarted);
        phaseTimings.CaptureMs = phaseTimings.OverlayHideMs;
        phaseTimings.UiTreeChars = result.Ui.Json.Length;
        phaseTimings.UiTreeNodes = ComputerUseUiTreeMetrics.CountNodes(result.Ui.Json);
        return await BuildObservationAsync(result, cancellationToken).ConfigureAwait(false);
    }

    private CapturedState CaptureState(
        bool includeUiTree,
        int maxDepth,
        int maxNodes,
        int? monitorX = null,
        int? monitorY = null,
        ComputerUseObserveTarget? target = null)
    {
        var (desktop, rootHandle) = ResolveDesktopCapture(monitorX, monitorY, target);
        var foreground = uiAutomationService.Capture(
            includeUiTree ? maxDepth : 1,
            includeUiTree ? maxNodes : 1,
            desktop.Left,
            desktop.Top,
            desktop.Width,
            desktop.Height,
            desktop.ImageWidth,
            desktop.ImageHeight,
            rootHandle);
        var ui = includeUiTree
            ? foreground
            : foreground with
            {
                Json = "[]",
                Elements = new Dictionary<string, AutomationElement>()
            };
        return new CapturedState(desktop, ui, target);
    }

    /// <summary>
    /// Picks the desktop region to capture. A window target wins over a monitor index, and both win
    /// over the cursor-monitor default, so the model can aim Computer Use at a specific app or
    /// display without the user first moving the pointer or raising the window. The returned handle
    /// is the window whose UI tree should be walked (default when the cursor monitor is used).
    /// </summary>
    private (ComputerUseCapturedDesktop Desktop, nint RootHandle) ResolveDesktopCapture(
        int? monitorX,
        int? monitorY,
        ComputerUseObserveTarget? target)
    {
        if (target is not null)
        {
            if (!string.IsNullOrWhiteSpace(target.WindowTitle))
            {
                if (!ComputerUseCaptureService.TryFindWindowBounds(
                        target.WindowTitle!,
                        target.WindowProcessName,
                        out var windowBounds,
                        out var windowHandle))
                {
                    throw new ComputerUseException(
                        "window_not_found",
                        $"No visible window matching '{target.WindowTitle}'.");
                }

                // DPI is only used for reporting; a window rect is already in physical pixels.
                return (
                    captureService.CaptureRect(
                        windowBounds.Left,
                        windowBounds.Top,
                        windowBounds.Width,
                        windowBounds.Height,
                        ResolveMonitorDpiScale(windowBounds),
                        null,
                        null),
                    windowHandle);
            }

            if (target.MonitorIndex is int index)
            {
                return (captureService.CaptureMonitorIndex(index), IntPtr.Zero);
            }
        }

        return (
            monitorX is int x && monitorY is int y
                ? captureService.CaptureAt(x, y)
                : captureService.CaptureCursorMonitor(),
            IntPtr.Zero);
    }

    private static double ResolveMonitorDpiScale(ComputerUseMonitorBounds bounds)
    {
        try
        {
            return ComputerUseCaptureService.ProbeDpiScaleAt(
                bounds.Left + (bounds.Width / 2),
                bounds.Top + (bounds.Height / 2));
        }
        catch
        {
            return 1;
        }
    }

    private Task<CapturedState> CaptureStateAsync(
        bool includeUiTree,
        int maxDepth,
        int maxNodes,
        CancellationToken cancellationToken,
        int? monitorX = null,
        int? monitorY = null,
        ComputerUseObserveTarget? target = null) =>
        RunBoundedUiAutomationAsync(
            () => CaptureState(includeUiTree, maxDepth, maxNodes, monitorX, monitorY, target),
            cancellationToken);

    private TimeSpan UiaCallTimeout => _settings.UiaCallTimeoutMs > 0
        ? TimeSpan.FromMilliseconds(_settings.UiaCallTimeoutMs)
        : TimeSpan.FromSeconds(5);

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
                .WaitAsync(UiaCallTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ComputerUseException(
                "uia_timeout",
                $"Windows UI Automation did not respond within {(int)UiaCallTimeout.TotalSeconds} seconds.");
        }
    }

    private async Task<ComputerUseObservation> BuildObservationAsync(
        CapturedState? state,
        CancellationToken cancellationToken,
        string? appliedAction = null,
        string? usedElementId = null,
        int? resolvedX = null,
        int? resolvedY = null,
        string? resolvedVia = null)
    {
        var sessionId = runContextAccessor.Current?.SessionId;
        var runId = runContextAccessor.Current?.RunId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(runId))
        {
            throw new ComputerUseException(
                "invalid_args",
                "Computer Use requires an active agent run context.");
        }

        if (state is null)
        {
            // Keyboard-only action with screenshot suppression: reuse the previous frame geometry
            // so the model can keep chaining keystrokes without a redundant observation round trip.
            var previous = _latestFrame;
            if (previous is null)
            {
                throw new ComputerUseException(
                    "stale_frame",
                    "No previous frame is available; call computer_observe.");
            }

            return new ComputerUseObservation(
                previous.FrameId,
                Screenshot: null,
                previous.Left,
                previous.Top,
                previous.Width,
                previous.Height,
                previous.DpiScale,
                previous.CursorX,
                previous.CursorY,
                previous.ForegroundWindowTitle,
                previous.ForegroundProcessName,
                "[]",
                previous.ImageWidth,
                previous.ImageHeight,
                appliedAction,
                usedElementId,
                resolvedX,
                resolvedY,
                resolvedVia);
        }

        var frameId = $"frame_{Guid.NewGuid():N}";
        var extension = state.Desktop.MimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            ? ".jpg"
            : ".png";
        var screenshot = imageAttachmentStore.SaveByteFrame(
            sessionId,
            $"{frameId}{extension}",
            state.Desktop.MimeType,
            state.Desktop.ImageBytes);
        // Frame screenshots accumulate one file per observation; trim old ones opportunistically.
        await attachmentPruner.PruneAsync(sessionId, cancellationToken).ConfigureAwait(false);
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
            state.Desktop.DpiScale,
            state.Desktop.CursorX,
            state.Desktop.CursorY,
            state.Ui.ForegroundWindowTitle,
            state.Ui.ForegroundProcessName,
            state.Ui.ForegroundWindowHandle,
            DateTimeOffset.UtcNow,
            state.Ui.Elements,
            state.ObserveTarget);

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
            resolvedY,
            resolvedVia);
    }
}
