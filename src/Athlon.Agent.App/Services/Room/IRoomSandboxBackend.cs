namespace Athlon.Agent.App.Services.Room;

public sealed class RoomSandboxProbeResult
{
    public bool Available { get; init; }

    public bool ImageReady { get; init; }

    public string? DockerVersion { get; init; }

    public string Message { get; init; } = "";

    public string? QuotaHint { get; init; }

    public IReadOnlyList<string> RuntimeHints { get; init; } =
        ["python3.12", "java8", "node24", "chromium"];
}

public sealed class RoomSandboxExecResult
{
    public int ExitCode { get; init; }

    public string Stdout { get; init; } = "";

    public string Stderr { get; init; } = "";
}

/// <summary>Local Docker or cloud sandbox execution surface for room @agent tools.</summary>
public interface IRoomSandboxBackend
{
    Task<RoomSandboxProbeResult> ProbeAsync(CancellationToken cancellationToken = default);

    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    Task EnsureRoomSessionAsync(
        string roomId,
        string hostWorkspaceRoot,
        string hostSkillsRoot,
        IReadOnlyList<string>? skillNames = null,
        CancellationToken cancellationToken = default);

    /// <summary>Pull remote workspace changes back to the host (cloud). Local Docker is a no-op.</summary>
    Task PullWorkspaceAsync(
        string roomId,
        string hostWorkspaceRoot,
        CancellationToken cancellationToken = default);

    Task DisposeRoomAsync(string roomId, CancellationToken cancellationToken = default);

    Task<RoomSandboxExecResult> ExecAsync(
        string roomId,
        string command,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken = default);

    Task<byte[]?> ReadFileAsync(string roomId, string containerPath, CancellationToken cancellationToken = default);

    Task WriteFileAsync(
        string roomId,
        string containerPath,
        byte[] content,
        CancellationToken cancellationToken = default);

    Task EnsureDirectoryAsync(
        string roomId,
        string containerDirectory,
        CancellationToken cancellationToken = default);

    Task<bool> PathExistsAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken = default);

    Task<bool> IsDirectoryAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListDirectoryAsync(
        string roomId,
        string containerPath,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves the sandbox backend for a room runtime mode.</summary>
public interface IRoomSandboxBackendResolver
{
    IRoomSandboxBackend? Resolve(string? agentRuntimeMode);

    IRoomSandboxBackend LocalDocker { get; }

    IRoomSandboxBackend Cloud { get; }
}
