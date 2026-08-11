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
                ?? throw new InvalidOperationException("Computer Use requires an active agent run context.");
            var frame = _latestFrame;
            if (frame is null
                || !string.Equals(request.FrameId, frame.FrameId, StringComparison.Ordinal)
                || !string.Equals(run.SessionId, frame.SessionId, StringComparison.Ordinal)
                || !string.Equals(run.RunId, frame.RunId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "stale_frame: the desktop changed or this frame is not current; call computer_observe again.");
            }

            var x = request.X ?? 0;
            var y = request.Y ?? 0;
            AutomationElement? target = null;
            if (!string.IsNullOrWhiteSpace(request.ElementId))
            {
                if (!frame.Elements.TryGetValue(request.ElementId, out target))
                {
                    throw new InvalidOperationException(
                        $"Unknown element_id '{request.ElementId}' for frame '{request.FrameId}'.");
                }

                if (request.Action is "click" or "double_click" or "right_click" or "scroll" or "drag"
                    or "type_text" or "key" or "hotkey")
                {
                    var point = await RunBoundedUiAutomationAsync(
                        () => uiAutomationService.TryGetClickablePoint(target!, out var px, out var py)
                            ? new ClickPoint(px, py)
                            : null,
                        cancellationToken).ConfigureAwait(false);
                    if (point is null)
                    {
                        throw new InvalidOperationException(
                            $"Element '{request.ElementId}' no longer has a clickable point.");
                    }

                    x = point.X;
                    y = point.Y;
                }
            }

            if (request.Action is "click" or "double_click" or "right_click" or "scroll" or "drag"
                && target is null
                && (request.X is null || request.Y is null))
            {
                throw new ArgumentException(
                    $"{request.Action} requires element_id or physical x/y coordinates.");
            }

            // A frame authorizes at most one atomic interaction, even when execution later fails.
            _latestFrame = null;
            var result = await overlayRegistry.RunWithOverlayHiddenAsync(async ct =>
            {
                ct.ThrowIfCancellationRequested();
                var currentDesktop = await CaptureStateAsync(
                    includeUiTree: false,
                    maxDepth: 1,
                    maxNodes: 20,
                    ct).ConfigureAwait(false);
                if (!MatchesFrame(frame, currentDesktop))
                {
                    throw new InvalidOperationException(
                        "stale_frame: the visible desktop changed since observation; call computer_observe again.");
                }

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
                    request.EndX,
                    request.EndY,
                    request.Text,
                    request.Key,
                    request.ScrollDelta,
                    CancellationToken.None).ConfigureAwait(false);
                // Once input starts, complete observation even if the caller cancels; never report a
                // cancellable half-action that could be retried against the same frame.
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                return await CaptureStateAsync(
                    includeUiTree: true,
                    maxDepth: 6,
                    maxNodes: 300,
                    CancellationToken.None).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            await auditLog.WriteAsync(
                "computer_interact",
                new
                {
                    request.Action,
                    request.FrameId,
                    request.ElementId,
                    x,
                    y,
                    request.EndX,
                    request.EndY,
                    foreground_window = result.Ui.ForegroundWindowTitle
                },
                CancellationToken.None).ConfigureAwait(false);

            return BuildObservation(result);
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

    private CapturedState CaptureState(bool includeUiTree, int maxDepth, int maxNodes)
    {
        var desktop = captureService.CaptureCursorMonitor();
        var foreground = uiAutomationService.Capture(
            includeUiTree ? maxDepth : 1,
            includeUiTree ? maxNodes : 1);
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
        CancellationToken cancellationToken) =>
        RunBoundedUiAutomationAsync(
            () => CaptureState(includeUiTree, maxDepth, maxNodes),
            cancellationToken);

    private async Task<T> RunBoundedUiAutomationAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        if (!await _uiaSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
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
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                "Windows UI Automation did not respond within 5 seconds.",
                exception);
        }
    }

    private ComputerUseObservation BuildObservation(CapturedState state)
    {
        var sessionId = runContextAccessor.Current?.SessionId;
        var runId = runContextAccessor.Current?.RunId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(runId))
        {
            throw new InvalidOperationException("Computer Use requires an active agent run context.");
        }

        var frameId = $"frame_{Guid.NewGuid():N}";
        var screenshot = imageAttachmentStore.SaveBytes(
            sessionId,
            $"{frameId}.png",
            "image/png",
            state.Desktop.PngBytes);
        _latestFrame = new FrameState(
            frameId,
            sessionId,
            runId,
            state.Desktop.Left,
            state.Desktop.Top,
            state.Desktop.Width,
            state.Desktop.Height,
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
            state.Ui.Json);
    }

    private bool IsScreenStable(ref string? previousHash, ref int stableSamples)
    {
        var bytes = captureService.CaptureCursorMonitor().PngBytes;
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
            _ => throw new ArgumentException(
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
                throw new InvalidOperationException(
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
                    throw new ArgumentException(
                        $"{request.Condition} requires element_id or name.");
                }
                break;
            case "window_title":
                if (string.IsNullOrWhiteSpace(request.WindowTitle))
                {
                    throw new ArgumentException("window_title requires a non-empty window_title.");
                }
                break;
            case "screen_stable":
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported wait condition '{request.Condition}'.");
        }
    }

    private static bool MatchesFrame(FrameState frame, CapturedState current)
    {
        return DateTimeOffset.UtcNow - frame.CreatedAt <= TimeSpan.FromSeconds(10)
            && frame.Left == current.Desktop.Left
            && frame.Top == current.Desktop.Top
            && frame.Width == current.Desktop.Width
            && frame.Height == current.Desktop.Height
            && string.Equals(
                frame.ForegroundWindowTitle,
                current.Ui.ForegroundWindowTitle,
                StringComparison.Ordinal)
            && string.Equals(
                frame.ForegroundProcessName,
                current.Ui.ForegroundProcessName,
                StringComparison.Ordinal);
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
