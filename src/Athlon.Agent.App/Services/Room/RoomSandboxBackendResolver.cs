namespace Athlon.Agent.App.Services.Room;

public sealed class RoomSandboxBackendResolver : IRoomSandboxBackendResolver
{
    private readonly RoomDockerSandbox _localDocker;
    private readonly RoomCloudSandbox _cloud;

    public RoomSandboxBackendResolver(RoomDockerSandbox localDocker, RoomCloudSandbox cloud)
    {
        _localDocker = localDocker;
        _cloud = cloud;
    }

    public IRoomSandboxBackend LocalDocker => _localDocker;

    public IRoomSandboxBackend Cloud => _cloud;

    public IRoomSandboxBackend? Resolve(string? agentRuntimeMode)
    {
        var mode = RoomAgentRuntimeModes.Normalize(agentRuntimeMode);
        return mode switch
        {
            RoomAgentRuntimeModes.Sandbox => _localDocker,
            RoomAgentRuntimeModes.CloudSandbox => _cloud,
            _ => null
        };
    }
}
