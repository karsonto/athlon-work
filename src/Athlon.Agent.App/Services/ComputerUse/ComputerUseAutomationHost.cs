using System.Security.Cryptography;
using System.Windows.Automation;
using Athlon.Agent.Core;
using Athlon.Agent.Core.ComputerUse;
using Athlon.Agent.Infrastructure;

namespace Athlon.Agent.App.Services.ComputerUse;

public sealed partial class ComputerUseAutomationHost(
    ComputerUseCaptureService captureService,
    ComputerUseUiAutomationService uiAutomationService,
    ComputerUseInputService inputService,
    ComputerUseOverlayRegistry overlayRegistry,
    IImageAttachmentStore imageAttachmentStore,
    IAgentRunContextAccessor runContextAccessor,
    AuditLogService auditLog,
    AppSettings settings,
    IImageAttachmentPruner attachmentPruner) : IComputerUseAutomationHost
{
    private readonly ComputerUseSettings _settings = settings.ComputerUse;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _uiaSlots = new(
        Math.Max(1, settings.ComputerUse.UiaMaxConcurrentCalls),
        Math.Max(1, settings.ComputerUse.UiaMaxConcurrentCalls));
    private FrameState? _latestFrame;

    public async Task<ComputerUseObservation> ObserveAsync(
        ComputerUseObserveRequest request,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var phaseTimings = new ComputerUsePhaseTimings();
            var started = ComputerUseTiming.Stamp();
            var observation = await ObserveCoreAsync(request, phaseTimings, cancellationToken)
                .ConfigureAwait(false);
            phaseTimings.TotalMs = ComputerUseTiming.ElapsedMs(started);
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
                    observation.ForegroundProcessName,
                    ui_tree_nodes = phaseTimings.UiTreeNodes,
                    ui_tree_included = request.IncludeUiTree,
                    max_tree_depth = phaseTimings.MaxTreeDepth,
                    max_nodes = phaseTimings.MaxNodes,
                    timings = phaseTimings.ToPayload()
                },
                cancellationToken).ConfigureAwait(false);
            return observation;
        }
        finally
        {
            _operationGate.Release();
        }
    }


    private sealed record CapturedState(
        ComputerUseCapturedDesktop Desktop,
        ComputerUseUiSnapshot Ui,
        ComputerUseObserveTarget? ObserveTarget = null);

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
        double DpiScale,
        int CursorX,
        int CursorY,
        string ForegroundWindowTitle,
        string ForegroundProcessName,
        nint ForegroundWindowHandle,
        DateTimeOffset CreatedAt,
        IReadOnlyDictionary<string, AutomationElement> Elements,
        ComputerUseObserveTarget? ObserveTarget = null);

    private sealed record ClickPoint(int X, int Y);

    private sealed record WaitEvaluation(
        bool Satisfied,
        string? Hash,
        int StableSamples);
}
