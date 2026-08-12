namespace Athlon.Agent.Core.Terminal;

public sealed class NullTerminalWorkspaceState : ITerminalWorkspaceState
{
    public static NullTerminalWorkspaceState Instance { get; } = new();

    public bool HasOpenTerminalTab => false;
}

public sealed class NullTerminalAutomationHost : ITerminalAutomationHost
{
    public static NullTerminalAutomationHost Instance { get; } = new();

    public Task EnsureTerminalTabAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Terminal automation host is not available."));

    public Task<TerminalSessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<TerminalSessionInfo>(new InvalidOperationException("Terminal automation host is not available."));

    public Task SendInputAsync(string text, bool appendNewline = true, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Terminal automation host is not available."));

    public Task<TerminalOutputSnapshot> ReadOutputAsync(int maxChars = 8000, CancellationToken cancellationToken = default) =>
        Task.FromException<TerminalOutputSnapshot>(new InvalidOperationException("Terminal automation host is not available."));
}
