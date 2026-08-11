namespace Athlon.Agent.Core.ComputerUse;

public sealed class NullComputerUseAutomationHost : IComputerUseAutomationHost
{
    public static readonly NullComputerUseAutomationHost Instance = new();

    private NullComputerUseAutomationHost()
    {
    }

    public Task<ComputerUseObservation> ObserveAsync(
        ComputerUseObserveRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ComputerUseObservation>(Unavailable());

    public Task<ComputerUseObservation> InteractAsync(
        ComputerUseInteractRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ComputerUseObservation>(Unavailable());

    public Task<string> WaitAsync(
        ComputerUseWaitRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<string>(Unavailable());

    private static InvalidOperationException Unavailable() =>
        new("Computer Use automation is unavailable in this host.");
}
