namespace Athlon.Agent.Core.ComputerUse;

public sealed record ComputerUseObserveRequest(
    bool IncludeUiTree = true,
    int MaxTreeDepth = 6,
    int MaxNodes = 300);

public sealed record ComputerUseObservation(
    string FrameId,
    ImageAttachment Screenshot,
    int Left,
    int Top,
    int Width,
    int Height,
    double DpiScale,
    int CursorX,
    int CursorY,
    string ForegroundWindowTitle,
    string ForegroundProcessName,
    string UiTreeJson);

public sealed record ComputerUseInteractRequest(
    string FrameId,
    string Action,
    string? ElementId = null,
    int? X = null,
    int? Y = null,
    int? EndX = null,
    int? EndY = null,
    string? Text = null,
    string? Key = null,
    int ScrollDelta = 0);

public sealed record ComputerUseWaitRequest(
    string Condition,
    string? ElementId = null,
    string? Name = null,
    string? WindowTitle = null,
    int TimeoutMs = 5000);

public interface IComputerUseAutomationHost
{
    Task<ComputerUseObservation> ObserveAsync(
        ComputerUseObserveRequest request,
        CancellationToken cancellationToken = default);

    Task<ComputerUseObservation> InteractAsync(
        ComputerUseInteractRequest request,
        CancellationToken cancellationToken = default);

    Task<string> WaitAsync(
        ComputerUseWaitRequest request,
        CancellationToken cancellationToken = default);
}
