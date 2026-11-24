namespace Athlon.Agent.App.Services.Room;

public sealed class RoomDockerProbeResult
{
    public bool Available { get; init; }

    public bool ImageReady { get; init; }

    public string? DockerVersion { get; init; }

    public string Message { get; init; } = "";

    public IReadOnlyList<string> RuntimeHints { get; init; } =
        ["python3.12", "java8", "node20", "chromium"];
}

public sealed class RoomDockerExecResult
{
    public int ExitCode { get; init; }

    public string Stdout { get; init; } = "";

    public string Stderr { get; init; } = "";
}

public interface IRoomDockerSandbox
{
    Task<RoomDockerProbeResult> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>Ensure daemon reachable and sandbox image present (build if needed).</summary>
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    Task EnsureRoomContainerAsync(
        string roomId,
        string hostWorkspaceRoot,
        string hostSkillsRoot,
        CancellationToken cancellationToken = default);

    Task DisposeRoomAsync(string roomId, CancellationToken cancellationToken = default);

    Task<RoomDockerExecResult> ExecAsync(
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
