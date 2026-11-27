using System.Text;
using Athlon.Agent.App.Services.Room;
using Athlon.Agent.Core;
using Athlon.Agent.Core.Sso;
using Athlon.Agent.Infrastructure.CloudSandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace Athlon.Agent.Tests;

public sealed class RoomCloudSandboxTests
{
    [Fact]
    public void RuntimeModes_Normalize_CloudSandbox()
    {
        Assert.Equal(RoomAgentRuntimeModes.CloudSandbox, RoomAgentRuntimeModes.Normalize("cloud_sandbox"));
        Assert.Equal(RoomAgentRuntimeModes.CloudSandbox, RoomAgentRuntimeModes.Normalize("cloud"));
        Assert.True(RoomAgentRuntimeModes.IsCloudSandbox("cloud_sandbox"));
        Assert.True(RoomAgentRuntimeModes.IsSandbox("cloud_sandbox"));
        Assert.False(RoomAgentRuntimeModes.IsLocalDockerSandbox("cloud_sandbox"));
        Assert.Equal("云沙箱", RoomAgentRuntimeModes.DisplayLabel("cloud_sandbox"));
    }

    [Fact]
    public void EncodeBlobFieldName_IsAsciiSafeForChinesePath()
    {
        var path = "POC白皮书翻译版.pdf";
        var field = RoomCloudSandbox.EncodeBlobFieldName(path);
        Assert.StartsWith("blob:", field, StringComparison.Ordinal);
        Assert.True(field.All(static ch => ch <= 0x7F));
        var token = field["blob:".Length..];
        var pad = new string('=', (4 - token.Length % 4) % 4);
        var decoded = Encoding.UTF8.GetString(
            Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + pad));
        Assert.Equal(path, decoded);
    }

    [Fact]
    public void AesGcmFrame_Roundtrips_WithSessionAad()
    {
        var key = AesGcmFrame.ParseSharedKeyBase64(Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('k', 32))));
        var frame = AesGcmFrame.Seal(key, Encoding.UTF8.GetBytes("hello"), "/rpc", "sess-1");
        var plain = AesGcmFrame.Open(key, frame, "/rpc", "sess-1");
        Assert.Equal("hello", Encoding.UTF8.GetString(plain));
        Assert.ThrowsAny<Exception>(() => AesGcmFrame.Open(key, frame, "/rpc", "other"));
    }

    [Fact]
    public void CloudSandboxSettings_RequiresSharedKey()
    {
        var settings = new CloudSandboxSettings
        {
            BaseUrl = "http://127.0.0.1:9090",
            ApiKey = "k"
        };
        Assert.False(settings.IsConfigured);
        settings.SharedKey = Convert.ToBase64String(new byte[32]);
        Assert.True(settings.IsConfigured);
    }

    [Fact]
    public async Task Probe_WithoutBaseUrl_IsUnavailable()
    {
        var sut = new RoomCloudSandbox(new AppSettings(), NullLogger<RoomCloudSandbox>.Instance);
        var probe = await sut.ProbeAsync();
        Assert.False(probe.Available);
        Assert.Contains("BaseUrl", probe.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_WithoutApiKey_IsUnavailable()
    {
        var settings = new AppSettings
        {
            CloudSandbox = new CloudSandboxSettings
            {
                BaseUrl = "http://127.0.0.1:9",
                SharedKey = Convert.ToBase64String(new byte[32])
            }
        };
        var sut = new RoomCloudSandbox(settings, NullLogger<RoomCloudSandbox>.Instance);
        var probe = await sut.ProbeAsync();
        Assert.False(probe.Available);
        Assert.Contains("ApiKey", probe.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_WithoutSharedKey_IsUnavailable()
    {
        var settings = new AppSettings
        {
            CloudSandbox = new CloudSandboxSettings
            {
                BaseUrl = "http://127.0.0.1:9",
                ApiKey = "test-key"
            }
        };
        var sut = new RoomCloudSandbox(settings, NullLogger<RoomCloudSandbox>.Instance);
        var probe = await sut.ProbeAsync();
        Assert.False(probe.Available);
        Assert.Contains("SharedKey", probe.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackDirectory_IgnoresNodeModules()
    {
        using var dir = new TempDirectoryScope("pack");
        File.WriteAllText(Path.Combine(dir.Root, "keep.txt"), "x");
        Directory.CreateDirectory(Path.Combine(dir.Root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(dir.Root, "node_modules", "pkg", "index.js"), "y");

        using var tar = CloudWorkspaceSync.PackDirectoryTarGz(dir.Root);
        using var unpack = new TempDirectoryScope("unpack");
        CloudWorkspaceSync.UnpackTarGzToDirectory(tar, unpack.Root);

        Assert.True(File.Exists(Path.Combine(unpack.Root, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(unpack.Root, "node_modules")));
    }

    [Fact]
    public void BackendResolver_SelectsByMode()
    {
        var docker = new RoomDockerSandbox(NullLogger<RoomDockerSandbox>.Instance);
        var cloud = new RoomCloudSandbox(
            new AppSettings
            {
                CloudSandbox = new CloudSandboxSettings
                {
                    BaseUrl = "http://x",
                    ApiKey = "k",
                    SharedKey = Convert.ToBase64String(new byte[32])
                }
            },
            NullLogger<RoomCloudSandbox>.Instance);
        var resolver = new RoomSandboxBackendResolver(docker, cloud);

        Assert.Same(docker, resolver.Resolve(RoomAgentRuntimeModes.Sandbox));
        Assert.Same(cloud, resolver.Resolve(RoomAgentRuntimeModes.CloudSandbox));
        Assert.Null(resolver.Resolve(RoomAgentRuntimeModes.Local));
    }
}
