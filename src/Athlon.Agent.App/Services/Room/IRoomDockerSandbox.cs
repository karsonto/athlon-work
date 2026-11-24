namespace Athlon.Agent.App.Services.Room;

/// <summary>Legacy name for <see cref="RoomSandboxProbeResult"/>.</summary>
public sealed class RoomDockerProbeResult
{
    public bool Available { get; init; }

    public bool ImageReady { get; init; }

    public string? DockerVersion { get; init; }

    public string Message { get; init; } = "";

    public string? QuotaHint { get; init; }

    public IReadOnlyList<string> RuntimeHints { get; init; } =
        ["python3.12", "java8", "node20", "chromium"];

    public RoomSandboxProbeResult ToSandboxProbe() =>
        new()
        {
            Available = Available,
            ImageReady = ImageReady,
            DockerVersion = DockerVersion,
            Message = Message,
            QuotaHint = QuotaHint,
            RuntimeHints = RuntimeHints
        };

    public static RoomDockerProbeResult From(RoomSandboxProbeResult value) =>
        new()
        {
            Available = value.Available,
            ImageReady = value.ImageReady,
            DockerVersion = value.DockerVersion,
            Message = value.Message,
            QuotaHint = value.QuotaHint,
            RuntimeHints = value.RuntimeHints
        };
}

/// <summary>Legacy name for <see cref="RoomSandboxExecResult"/>.</summary>
public sealed class RoomDockerExecResult
{
    public int ExitCode { get; init; }

    public string Stdout { get; init; } = "";

    public string Stderr { get; init; } = "";

    public RoomSandboxExecResult ToSandboxExec() =>
        new() { ExitCode = ExitCode, Stdout = Stdout, Stderr = Stderr };

    public static RoomDockerExecResult From(RoomSandboxExecResult value) =>
        new() { ExitCode = value.ExitCode, Stdout = value.Stdout, Stderr = value.Stderr };
}

/// <inheritdoc cref="IRoomSandboxBackend" />
public interface IRoomDockerSandbox : IRoomSandboxBackend
{
    /// <summary>Prefer <see cref="IRoomSandboxBackend.EnsureRoomSessionAsync"/>.</summary>
    Task EnsureRoomContainerAsync(
        string roomId,
        string hostWorkspaceRoot,
        string hostSkillsRoot,
        CancellationToken cancellationToken = default);
}
