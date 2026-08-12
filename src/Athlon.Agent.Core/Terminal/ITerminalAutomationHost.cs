namespace Athlon.Agent.Core.Terminal;

public sealed record TerminalSessionInfo(
    string Title,
    string? WorkingDirectory,
    bool IsAttached,
    bool ProcessAlive);

public sealed record TerminalOutputSnapshot(
    string Text,
    bool Truncated,
    int TotalChars);

/// <summary>UI-agnostic host for workspace Terminal tab ConPTY automation.</summary>
public interface ITerminalAutomationHost
{
    Task EnsureTerminalTabAsync(CancellationToken cancellationToken = default);

    Task<TerminalSessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default);

    Task SendInputAsync(string text, bool appendNewline = true, CancellationToken cancellationToken = default);

    Task<TerminalOutputSnapshot> ReadOutputAsync(int maxChars = 8000, CancellationToken cancellationToken = default);
}
