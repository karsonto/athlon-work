using System.Security.Cryptography;
using System.Windows.Automation;
using Athlon.Agent.Core;
using Athlon.Agent.Core.ComputerUse;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.ComputerUse;

/// <summary>Wait-until conditions over the live desktop.</summary>
public sealed partial class ComputerUseAutomationHost
{
    public async Task<string> WaitAsync(
        ComputerUseWaitRequest request,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateWaitRequest(request);
            var waitStarted = ComputerUseTiming.Stamp();
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
                    request.TimeoutMs,
                    elapsed_ms = Math.Round(ComputerUseTiming.ElapsedMs(waitStarted), 1)
                },
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
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

    private static (int EndX, int EndY) ResolveDragEnd(
        ComputerUseInteractRequest request,
        FrameState frame)
    {
        if (request.EndImageX is int endImageX && request.EndImageY is int endImageY)
        {
            return ComputerUseCoordinateMapper.ImageToPhysical(
                endImageX,
                endImageY,
                frame.Left,
                frame.Top,
                frame.CaptureWidth,
                frame.CaptureHeight,
                frame.ImageWidth,
                frame.ImageHeight);
        }

        if (request.EndX is int endX && request.EndY is int endY)
        {
            return (endX, endY);
        }

        throw new ComputerUseException(
            "invalid_args",
            "drag requires end_image_x/end_image_y or physical end_x/end_y.");
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
}
