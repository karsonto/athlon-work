using Athlon.Agent.App.Services.Room;
using Athlon.Agent.Core;
using Athlon.Agent.Infrastructure.Ssh;

namespace Athlon.Agent.Tests;

public sealed class RoomWorkspaceBindingTests
{
    [Fact]
    public void Validate_SshWorkspace_ById_Succeeds_WithoutLocalDirectory()
    {
        var settings = CreateSettingsWithSsh(
            id: "ssh-1",
            root: "/home/remote/project",
            host: "example.com",
            user: "deploy");

        var error = RoomWorkspaceBinding.Validate(
            settings,
            workspaceId: "ssh-1",
            workspaceRoot: null,
            agentRuntimeMode: RoomAgentRuntimeModes.Local);

        Assert.Null(error);
    }

    [Fact]
    public void Validate_SshPlusSandbox_ReturnsUnsupported()
    {
        var settings = CreateSettingsWithSsh(
            id: "ssh-1",
            root: "/home/remote/project",
            host: "example.com",
            user: "deploy");

        Assert.Equal(
            "ssh_workspace_sandbox_unsupported",
            RoomWorkspaceBinding.Validate(
                settings,
                "ssh-1",
                null,
                RoomAgentRuntimeModes.Sandbox));

        Assert.Equal(
            "ssh_workspace_sandbox_unsupported",
            RoomWorkspaceBinding.Validate(
                settings,
                "ssh-1",
                null,
                RoomAgentRuntimeModes.CloudSandbox));
    }

    [Fact]
    public void Validate_SshMissingHost_Invalid()
    {
        var settings = new AppSettings
        {
            Workspaces =
            [
                new WorkspaceSettings
                {
                    Id = "ssh-bad",
                    Name = "Broken",
                    Kind = WorkspaceKinds.Ssh,
                    RootPath = "/opt/app",
                    Ssh = new SshWorkspaceSettings
                    {
                        Host = "",
                        Username = "u"
                    }
                }
            ]
        };

        Assert.Equal(
            "ssh_workspace_invalid",
            RoomWorkspaceBinding.Validate(settings, "ssh-bad", null, RoomAgentRuntimeModes.Local));
    }

    [Fact]
    public void Validate_LocalMissingPath_NotFound()
    {
        var settings = new AppSettings();
        var missing = Path.Combine(Path.GetTempPath(), "athlon-missing-ws-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(
            "workspace_not_found",
            RoomWorkspaceBinding.Validate(settings, null, missing, RoomAgentRuntimeModes.Local));
    }

    [Fact]
    public void Validate_LocalExistingPath_Ok_ForSandboxModes()
    {
        using var temp = new TempDirectoryScope("athlon-room-ws");
        var settings = new AppSettings();

        Assert.Null(RoomWorkspaceBinding.Validate(
            settings, null, temp.Root, RoomAgentRuntimeModes.Local));
        Assert.Null(RoomWorkspaceBinding.Validate(
            settings, null, temp.Root, RoomAgentRuntimeModes.Sandbox));
        Assert.Null(RoomWorkspaceBinding.Validate(
            settings, null, temp.Root, RoomAgentRuntimeModes.CloudSandbox));
    }

    [Fact]
    public void Resolve_PrefersWorkspaceId_OverRootGuess()
    {
        var settings = CreateSettingsWithSsh(
            id: "ssh-1",
            root: "/home/remote/project",
            host: "h",
            user: "u");

        var binding = RoomWorkspaceBinding.Resolve(settings, "ssh-1", "/some/other");
        Assert.True(binding.IsSsh);
        Assert.Equal("ssh-1", binding.Id);
        Assert.Equal("/home/remote/project", binding.Root);
    }

    [Fact]
    public void AttachmentMirrorDir_Ssh_UsesForwardSlashes()
    {
        var dir = RoomWorkspaceBinding.AttachmentMirrorDir(
            "/home/remote/project",
            "roomABC",
            WorkspaceKind.Ssh);

        Assert.Equal("/home/remote/project/user-upload", dir);
        Assert.DoesNotContain('\\', dir);
    }

    [Fact]
    public void FormatDisplayLabel_IncludesHost()
    {
        var label = RoomWorkspaceBinding.FormatDisplayLabel(new WorkspaceSettings
        {
            Name = "Prod",
            Kind = WorkspaceKinds.Ssh,
            RootPath = "/var/www",
            Ssh = new SshWorkspaceSettings { Host = "box.example", Username = "ops" }
        });

        Assert.Contains("[SSH]", label);
        Assert.Contains("box.example", label);
    }

    [Fact]
    public async Task SyncAsync_ForSshSession_CallsEnsureConnected()
    {
        var settings = CreateSettingsWithSsh(
            id: "ssh-sync",
            root: "/data/ws",
            host: "sync.host",
            user: "syncuser");

        var registry = new RecordingSshRegistry();
        var credentials = new DictionaryCredentialStore();
        credentials.Set("ssh-password:ssh-sync", "secret");
        var logger = new NullAppLogger();
        var connection = new SshWorkspaceConnectionService(registry, credentials, logger);

        var session = AgentSession.Create("Room test") with { Id = "room-agent-r1" };
        session = session.WithWorkspace("/data/ws", "ssh-sync");

        // BuildRequest will succeed; EnsureConnected is recorded (no real SSH).
        await connection.SyncAsync(session, settings);

        Assert.Contains("room-agent-r1", registry.EnsureConnectedSessionIds);
        var request = Assert.Single(registry.LastRequests);
        Assert.Equal("sync.host", request.Host);
        Assert.Equal("syncuser", request.Username);
    }

    private static AppSettings CreateSettingsWithSsh(string id, string root, string host, string user) =>
        new()
        {
            Workspaces =
            [
                new WorkspaceSettings
                {
                    Id = id,
                    Name = "Remote",
                    Kind = WorkspaceKinds.Ssh,
                    RootPath = root,
                    Ssh = new SshWorkspaceSettings
                    {
                        Host = host,
                        Username = user,
                        Port = 22,
                        AuthMode = "password"
                    }
                }
            ]
        };

    private sealed class RecordingSshRegistry : ISshConnectionRegistry
    {
        public string? DefaultSessionId { get; set; }
        public List<string> EnsureConnectedSessionIds { get; } = [];
        public List<SshConnectRequest> LastRequests { get; } = [];

        public bool IsSessionConnected(string rootSessionId) => false;

        public Task EnsureConnectedAsync(
            string rootSessionId,
            SshConnectRequest request,
            CancellationToken cancellationToken = default)
        {
            EnsureConnectedSessionIds.Add(rootSessionId);
            LastRequests.Add(request);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(string rootSessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DisconnectAllAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class DictionaryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

        public void Set(string name, string value) => _map[name] = value;

        public Task SaveSecretAsync(string name, string secret, CancellationToken cancellationToken = default)
        {
            _map[name] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_map.TryGetValue(name, out var v) ? v : null);

        public Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_map.ContainsKey(name));

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default)
        {
            _map.Remove(name);
            return Task.CompletedTask;
        }
    }

    private sealed class NullAppLogger : IAppLogger
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(Exception exception, string messageTemplate, params object[] values) { }
        public IAppLogger ForContext(string sourceContext) => this;
    }
}
